using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 面对面四位置基座的二次生成事务编排。
    /// 总装若引用了本次生成所需的同名模板，先解除模板引用，再准备全部模型并统一回装；
    /// 总装验证、重建和保存成功前，不提交本次预留的图号。
    /// </summary>
    internal sealed class FaceToFaceBaseSecondaryDriveService
    {
        private static readonly string[] DefaultPositionOrder =
        {
            "A柜前", "A柜后", "B柜前", "B柜后"
        };

        private readonly IFaceToFaceBaseSecondaryOperations _operations;
        private readonly IBaseModelLifecycleTransaction _transaction;
        private readonly IFaceToFaceBaseTemplateCopier _templateCopier;
        private readonly IFaceToFaceBaseDriver _baseDriver;
        private readonly IFaceToFaceBaseExistingInsulatorDriver _insulatorDriver;
        private readonly IFaceToFaceBaseDirectoryCleaner _directoryCleaner;
        private readonly Func<
            CabinetDriverParameterSnapshot,
            IReadOnlyList<FaceToFaceBasePositionPlan>> _planFactory;
        private readonly IReadOnlyList<string> _positionOrder;
        private readonly IReadOnlyList<string> _mappingOrder;
        private readonly bool _driveInsulatorBeforeBase;

        internal FaceToFaceBaseSecondaryDriveService(
            IFaceToFaceBaseSecondaryOperations operations,
            IFaceToFaceBaseTemplateCopier templateCopier,
            IFaceToFaceBaseDriver baseDriver,
            IFaceToFaceBaseExistingInsulatorDriver insulatorDriver,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner)
            : this(
                operations,
                templateCopier,
                baseDriver,
                insulatorDriver,
                directoryCleaner,
                FaceToFaceBaseDrivePlanner.CreatePlans,
                DefaultPositionOrder,
                FaceToFaceBaseModelMappingHelper.FaceToFaceOrderedPositions,
                driveInsulatorBeforeBase: false)
        {
        }

        /// <summary>
        /// 场景化构造入口。面对墙提供柜前/柜后计划和映射顺序，并要求绝缘子先于基座驱动。
        /// </summary>
        internal FaceToFaceBaseSecondaryDriveService(
            IFaceToFaceBaseSecondaryOperations operations,
            IFaceToFaceBaseTemplateCopier templateCopier,
            IFaceToFaceBaseDriver baseDriver,
            IFaceToFaceBaseExistingInsulatorDriver insulatorDriver,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner,
            Func<CabinetDriverParameterSnapshot, IReadOnlyList<FaceToFaceBasePositionPlan>> planFactory,
            IReadOnlyList<string> positionOrder,
            IReadOnlyList<string> mappingOrder,
            bool driveInsulatorBeforeBase)
        {
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            _transaction = new FaceToFaceBaseLifecycleTransactionAdapter(_operations);
            _templateCopier = templateCopier
                ?? throw new ArgumentNullException(nameof(templateCopier));
            _baseDriver = baseDriver ?? throw new ArgumentNullException(nameof(baseDriver));
            _insulatorDriver = insulatorDriver
                ?? throw new ArgumentNullException(nameof(insulatorDriver));
            _directoryCleaner = directoryCleaner
                ?? throw new ArgumentNullException(nameof(directoryCleaner));
            _planFactory = planFactory ?? throw new ArgumentNullException(nameof(planFactory));
            _positionOrder = positionOrder ?? throw new ArgumentNullException(nameof(positionOrder));
            _mappingOrder = mappingOrder ?? throw new ArgumentNullException(nameof(mappingOrder));
            _driveInsulatorBeforeBase = driveInsulatorBeforeBase;
        }

        internal FaceToFaceBaseSecondaryDriveResult Update(
            string projectPath,
            FaceToFaceBaseSecondarySnapshot snapshot,
            CabinetDriverParameters currentParameters,
            IDrawingNumberGenerator drawingNumbers)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("项目路径不能为空。", nameof(projectPath));
            }

            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (currentParameters == null)
            {
                throw new ArgumentNullException(nameof(currentParameters));
            }

            if (drawingNumbers == null)
            {
                throw new ArgumentNullException(nameof(drawingNumbers));
            }

            var fullProjectPath = Path.GetFullPath(projectPath);
            if (!Directory.Exists(fullProjectPath))
            {
                throw new DirectoryNotFoundException("项目目录不存在：" + fullProjectPath);
            }

            // 开始修改前重新读取一次总装，拒绝使用已经过期的界面快照。
            var liveSnapshot = _operations.CaptureCurrentSnapshot(snapshot.Parameters);
            FaceToFaceBaseSecondarySnapshotComparer.EnsureSamePhysicalState(
                snapshot,
                liveSnapshot);

            // 旧、新计划必须使用同一场景适配器；旧宿主缺失也须在保存、拆卸或改名前报错。
            var oldPositionPlans = CreateOldPositionPlans(liveSnapshot);
            var targetParameters = CabinetDriverParameterSnapshot.Capture(currentParameters);
            var targetPlans = _planFactory(targetParameters);
            var drivePlan = FaceToFaceBaseSecondaryDrivePlanner.Create(
                liveSnapshot,
                targetPlans,
                _positionOrder);
            if (drivePlan.IsEmpty)
            {
                return FaceToFaceBaseSecondaryDriveResult.NoChanges(liveSnapshot);
            }

            // 新同源组必须从模板生成到全新目录，事务开始前拒绝覆盖任何遗留内容。
            EnsureTemplateTargetDirectoriesAvailable(fullProjectPath, drivePlan);

            var transactionalNumbers = new DeferredDrawingNumberGenerator(drawingNumbers);
            var preparedModels = new Dictionary<string, PreparedModel>(StringComparer.Ordinal);
            var obsoleteFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            var drivenCount = 0;
            var createdCount = 0;
            var replacedCount = 0;
            var baselineSaved = false;
            var totalCommitted = false;
            var finalAssemblyCommitFailed = false;

            try
            {
                _transaction.SaveBaseline();
                baselineSaved = true;
                var transactionInsulatorDriver = new TransactionalInsulatorDriver(
                    _insulatorDriver,
                    _transaction,
                    fullProjectPath);
                var firstFrameAnchors = new Dictionary<
                    string,
                    FaceToFaceBaseFirstFrameAnchor>(StringComparer.Ordinal);
                var preDetachedPositions = DetachLoadedTemplatePositions(
                    liveSnapshot,
                    drivePlan,
                    firstFrameAnchors);

                // 模板引用已纳入事务基线保护；所有目标模型仍须全部准备成功后再统一回装。
                foreach (var action in drivePlan.ModelActions)
                {
                    PreparedModel prepared;
                    if (action.SourceKind == FaceToFaceBaseModelSourceKind.Existing)
                    {
                        prepared = PrepareExistingModel(
                            fullProjectPath,
                            liveSnapshot,
                            oldPositionPlans,
                            action,
                            transactionalNumbers,
                            obsoleteFiles,
                            transactionInsulatorDriver);
                        if (action.NeedsDrive)
                        {
                            drivenCount++;
                        }
                    }
                    else
                    {
                        prepared = PrepareTemplateModel(
                            fullProjectPath,
                            action,
                            transactionalNumbers,
                            transactionInsulatorDriver);
                        createdCount++;
                    }

                    prepared.Kind = action.Kind;
                    foreach (var positionName in action.PositionNames)
                    {
                        prepared.PositionNames.Add(positionName);
                    }

                    preparedModels.Add(action.TargetKey, prepared);
                }

                var finalTokens = ReplaceChangedPositions(
                    drivePlan,
                    preparedModels,
                    preDetachedPositions,
                    firstFrameAnchors,
                    ref replacedCount);
                _operations.WriteMapping(FaceToFaceBaseModelMappingHelper.BuildJson(
                    finalTokens,
                    _mappingOrder));

                var committedSnapshot = _operations.ValidateFinalState(targetParameters);
                EnsureFinalState(targetPlans, preparedModels, committedSnapshot);
                var installationChanges = BaseInstallationChangeFactory.Create(liveSnapshot, committedSnapshot,
                    oldPositionPlans.Values.ToArray(), targetPlans, preDetachedPositions);
                try
                {
                    _operations.RebuildAndSaveTotalAssembly();
                }
                catch
                {
                    // 最终总装已包含全部回装结果；提交重建失败时保留现场供用户处理。
                    finalAssemblyCommitFailed = true;
                    throw;
                }

                // 图号只在总装已经完成验证并保存后推进；此前任何失败都不会消耗图号。
                transactionalNumbers.CommitAll();
                totalCommitted = true;

                var deletedCount = CleanupRetiredArtifacts(
                    fullProjectPath,
                    drivePlan,
                    obsoleteFiles,
                    warnings);
                TryClearTransactionArtifacts(warnings);

                return new FaceToFaceBaseSecondaryDriveResult(
                    true,
                    drivenCount,
                    createdCount,
                    replacedCount,
                    deletedCount,
                    warnings,
                    committedSnapshot,
                    installationChanges);
            }
            catch (Exception originalException)
            {
                if (!finalAssemblyCommitFailed && !totalCommitted && baselineSaved)
                {
                    try
                    {
                        _transaction.Restore();
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException(
                            originalException.Message
                                + Environment.NewLine
                                + "基座二次生成回滚失败："
                                + rollbackException.Message,
                            originalException);
                    }
                }

                throw new InvalidOperationException(
                    "基座二次生成失败：" + originalException.Message,
                    originalException);
            }
        }

        private PreparedModel PrepareExistingModel(
            string projectPath,
            FaceToFaceBaseSecondarySnapshot snapshot,
            IReadOnlyDictionary<string, FaceToFaceBasePositionPlan> oldPositionPlans,
            FaceToFaceBaseTargetModelAction action,
            DeferredDrawingNumberGenerator drawingNumbers,
            ISet<string> obsoleteFiles,
            IFaceToFaceBaseExistingInsulatorDriver insulatorDriver)
        {
            var source = action.ExistingModel;
            _transaction.BackupModelDirectory(source.ModelPath);
            var modelPath = source.ModelPath;

            if (action.NameAction != FaceToFaceBaseNameActionKind.Keep)
            {
                var suffix = action.NameAction == FaceToFaceBaseNameActionKind.AddDrawingNumber
                    ? drawingNumbers.ReserveNextFileNameSuffix()
                    : string.Empty;
                var normalized = _operations.NormalizeExistingModelNames(
                    modelPath,
                    _templateCopier.GetTemplateAssemblyPath(action.Kind),
                    suffix);
                modelPath = normalized.ModelPath;
                _transaction.RegisterCreatedFiles(normalized.CreatedFiles);
                foreach (var obsoleteFile in normalized.ObsoleteFiles)
                {
                    obsoleteFiles.Add(obsoleteFile);
                }
            }

            if (action.NeedsDrive)
            {
                var modelPlan = GetModelPlan(action.TargetPositionPlan, action.Kind);
                var hostedInsulator = GetHostedInsulatorPlan(
                    action.TargetPositionPlan,
                    action.Kind);
                var oldHostedInsulator = FindOldHostedInsulatorPlan(oldPositionPlans, source);
                if (hostedInsulator == null)
                {
                    _baseDriver.DriveExistingModel(modelPath, modelPlan);
                    RemoveHostedInsulatorIfPresent(
                        insulatorDriver,
                        modelPath,
                        oldHostedInsulator);
                }
                else if (InsulatorPlansEqual(oldHostedInsulator, hostedInsulator))
                {
                    // 绝缘子参数没有变化时只驱动基座，不重复打开和驱动绝缘子模型。
                    _baseDriver.DriveExistingModel(modelPath, modelPlan);
                }
                else
                {
                    var baseGeometryChanged = !string.Equals(
                        source.Identity.BaseValue,
                        action.TargetIdentity.BaseValue,
                        StringComparison.Ordinal);
                    var updater = new FaceToFaceBaseExistingModelUpdater(
                        _baseDriver,
                        insulatorDriver,
                        projectPath,
                        (path, plan) => CanDriveInsulatorInPlace(snapshot, source, path),
                        _driveInsulatorBeforeBase,
                        // 面对墙按脏分组局部驱动；面对面保留既有的整棵基座驱动行为。
                        driveBase: !_driveInsulatorBeforeBase || baseGeometryChanged);
                    updater.UpdateExistingModel(modelPath, modelPlan, hostedInsulator);
                }
            }

            return new PreparedModel(modelPath, action.TargetIdentity);
        }

        private PreparedModel PrepareTemplateModel(
            string projectPath,
            FaceToFaceBaseTargetModelAction action,
            IDrawingNumberGenerator drawingNumbers,
            IFaceToFaceBaseExistingInsulatorDriver insulatorDriver)
        {
            var modelPlan = GetModelPlan(action.TargetPositionPlan, action.Kind);
            var baseRoot = Path.Combine(projectPath, "基座");
            var finalDirectory = GetTemplateTargetDirectory(projectPath, action);
            _transaction.RegisterNewDirectory(finalDirectory);

            // 总装内的同名模板引用已在生成前解除并关闭；此处统一从只读模板缓存生成，
            // 避免 SolidWorks 同时打开两个同标题文档时误命中项目模型。
            var sourceAssemblyPath = Path.GetFullPath(
                _templateCopier.GetTemplateAssemblyPath(action.Kind));
            var modelPath = FaceToFaceBaseModelPreparation.PrepareNewModel(
                _templateCopier,
                _baseDriver,
                insulatorDriver,
                baseRoot,
                projectPath,
                action.TargetPositionPlan.PositionName,
                modelPlan,
                drawingNumbers,
                GetHostedInsulatorPlan(action.TargetPositionPlan, action.Kind),
                sourceAssemblyPath);
            if (Directory.Exists(finalDirectory))
            {
                _transaction.RegisterCreatedFiles(
                    Directory.GetFiles(finalDirectory, "*", SearchOption.AllDirectories));
            }

            return new PreparedModel(modelPath, action.TargetIdentity);
        }

        private ISet<string> DetachLoadedTemplatePositions(
            FaceToFaceBaseSecondarySnapshot snapshot,
            FaceToFaceBaseSecondaryDrivePlan drivePlan,
            IDictionary<string, FaceToFaceBaseFirstFrameAnchor> firstFrameAnchors)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (drivePlan == null)
            {
                throw new ArgumentNullException(nameof(drivePlan));
            }

            if (firstFrameAnchors == null)
            {
                throw new ArgumentNullException(nameof(firstFrameAnchors));
            }

            var templateKinds = new HashSet<FaceToFaceBaseModelKind>(
                drivePlan.ModelActions
                    .Where(action => action.SourceKind
                        == FaceToFaceBaseModelSourceKind.Template)
                    .Select(action => action.Kind));
            var preDetachedModelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 已有模型加/去图号后缀需要 PackAndGo 改名；只要总装仍引用该模型，SolidWorks 就可能
            // 因同标题文档被占用而拒绝打开或复制，因此必须先删除配合并移除组件。
            foreach (var action in drivePlan.ModelActions.Where(action =>
                action.SourceKind == FaceToFaceBaseModelSourceKind.Existing
                && action.NameAction != FaceToFaceBaseNameActionKind.Keep))
            {
                preDetachedModelPaths.Add(Path.GetFullPath(action.ExistingModel.ModelPath));
            }

            // 从模板创建新同源组时，项目总装中若仍装有同名模板，也必须先解除其全部引用，
            // 否则模板缓存中的源模型无法可靠打开。
            foreach (var kind in templateKinds)
            {
                var templateFileName = Path.GetFileName(
                    _templateCopier.GetTemplateAssemblyPath(kind));
                foreach (var model in snapshot.Models.Where(model =>
                    model.Kind == kind
                    && !model.NameState.HasDrawingNumberSuffix
                    && string.Equals(
                        Path.GetFileName(model.ModelPath),
                        templateFileName,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    preDetachedModelPaths.Add(Path.GetFullPath(model.ModelPath));
                }
            }

            var addingElevatedPositions = new HashSet<string>(
                drivePlan.PositionActions
                    .Where(action => action.OldElevatedBase == null
                        && !string.IsNullOrWhiteSpace(action.TargetElevatedModelKey))
                    .Select(action => action.PositionName),
                StringComparer.Ordinal);
            if (addingElevatedPositions.Count > 0)
            {
                // 新增增高基座前，目标位置的普通基座必须先离开总装；否则仍在装配中的
                // 普通基座会占用与增高基座模板同名的子件，导致新模型方程式绑定到旧文档。
                foreach (var model in snapshot.Models.Where(model =>
                    model.Kind == FaceToFaceBaseModelKind.Base
                    && addingElevatedPositions.Contains(model.PositionName)))
                {
                    preDetachedModelPaths.Add(Path.GetFullPath(model.ModelPath));
                }

                // 其它位置若仍直接使用普通基座模板，也必须一并临时拆下。它们可能位于
                // 不同项目目录，但根装配文件名及内部子件名仍与模板相同。
                var ordinaryTemplateFileName = Path.GetFileName(
                    _templateCopier.GetTemplateAssemblyPath(FaceToFaceBaseModelKind.Base));
                foreach (var model in snapshot.Models.Where(model =>
                    model.Kind == FaceToFaceBaseModelKind.Base
                    && !model.NameState.HasDrawingNumberSuffix
                    && string.Equals(
                        Path.GetFileName(model.ModelPath),
                        ordinaryTemplateFileName,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    preDetachedModelPaths.Add(Path.GetFullPath(model.ModelPath));
                }
            }

            var detachedPositions = new HashSet<string>(StringComparer.Ordinal);
            var affectedPositions = snapshot.Models
                .Where(model => preDetachedModelPaths.Contains(
                    Path.GetFullPath(model.ModelPath)))
                .Select(model => model.PositionName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(PositionPriority)
                .ToArray();
            foreach (var positionName in affectedPositions)
            {
                var chain = snapshot.Models.Where(model => string.Equals(
                    model.PositionName,
                    positionName,
                    StringComparison.Ordinal));
                // 普通基座可能配合在增高基座上，整条位置链必须先普通、后增高拆除。
                RemoveOldModel(chain.SingleOrDefault(model =>
                    model.Kind == FaceToFaceBaseModelKind.Base), firstFrameAnchors);
                RemoveOldModel(chain.SingleOrDefault(model =>
                    model.Kind == FaceToFaceBaseModelKind.ElevatedBase), firstFrameAnchors);
                detachedPositions.Add(positionName);
            }

            if (detachedPositions.Count > 0)
            {
                if (addingElevatedPositions.Count > 0)
                {
                    // 新增增高基座时，当前活动文档必须是总装；拆除后立即重建并保存，
                    // 使磁盘和内存引用树同步清除旧基座，再以实际组件树确认零引用。
                    _operations.RebuildAndSaveTotalAssembly();
                    EnsureDetachedModelsAreUnreferenced(
                        preDetachedModelPaths,
                        _operations.GetReferencedBaseModelPaths());
                }
                else
                {
                    // 其它既有预拆场景保持原事务行为，只刷新内存引用树。
                    _operations.RebuildTotalAssemblyAfterDetach();
                }
            }

            // 所有实例解除引用后再关闭精确文档，确保模板缓存中的同标题模型能够打开。
            foreach (var modelPath in preDetachedModelPaths.OrderBy(path => path))
            {
                _operations.CloseModelDocument(modelPath);
            }

            return detachedPositions;
        }

        private static void EnsureDetachedModelsAreUnreferenced(
            IEnumerable<string> detachedModelPaths,
            IEnumerable<string> referencedModelPaths)
        {
            if (detachedModelPaths == null)
            {
                throw new ArgumentNullException(nameof(detachedModelPaths));
            }

            if (referencedModelPaths == null)
            {
                throw new InvalidOperationException(
                    "保存总装配体后无法读取组件引用，禁止继续生成增高基座。");
            }

            var detached = new HashSet<string>(
                detachedModelPaths.Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            var remaining = referencedModelPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Where(detached.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (remaining.Length > 0)
            {
                throw new InvalidOperationException(
                    "拆除、重建并保存总装配体后，以下基座仍被总装配体引用："
                    + string.Join("；", remaining));
            }
        }

        private static void EnsureTemplateTargetDirectoriesAvailable(
            string projectPath,
            FaceToFaceBaseSecondaryDrivePlan drivePlan)
        {
            foreach (var action in drivePlan.ModelActions.Where(action =>
                action.SourceKind == FaceToFaceBaseModelSourceKind.Template))
            {
                var targetDirectory = GetTemplateTargetDirectory(projectPath, action);
                if (Directory.Exists(targetDirectory))
                {
                    throw new IOException(
                        "新增基座的模板目标目录已存在，拒绝覆盖：" + targetDirectory);
                }
            }
        }

        private static string GetTemplateTargetDirectory(
            string projectPath,
            FaceToFaceBaseTargetModelAction action)
        {
            var typeFolder = action.Kind == FaceToFaceBaseModelKind.Base
                ? "基座"
                : "增高基座";
            var baseRoot = Path.Combine(projectPath, "基座");
            return FaceToFaceBaseTransactionPathGuard.EnsureSafeDescendant(
                baseRoot,
                Path.Combine(
                    baseRoot,
                    action.TargetPositionPlan.PositionName,
                    typeFolder));
        }

        private IReadOnlyList<FaceToFaceBaseAssemblyToken> ReplaceChangedPositions(
            FaceToFaceBaseSecondaryDrivePlan drivePlan,
            IReadOnlyDictionary<string, PreparedModel> preparedModels,
            ISet<string> preDetachedPositions,
            IDictionary<string, FaceToFaceBaseFirstFrameAnchor> firstFrameAnchors,
            ref int replacedCount)
        {
            var tokens = new List<FaceToFaceBaseAssemblyToken>();
            foreach (var position in drivePlan.PositionActions.OrderBy(
                x => PositionPriority(x.PositionName)))
            {
                var ordinaryPath = ResolvePreparedPath(
                    preparedModels,
                    position.TargetOrdinaryModelKey);
                var elevatedPath = ResolvePreparedPath(
                    preparedModels,
                    position.TargetElevatedModelKey);
                var wasPreDetached = preDetachedPositions.Contains(position.PositionName);
                var chainChanged = wasPreDetached
                    || !SameOptionalPath(
                        position.OldOrdinaryBase?.ModelPath,
                        ordinaryPath)
                    || !SameOptionalPath(
                        position.OldElevatedBase?.ModelPath,
                        elevatedPath);

                if (!chainChanged)
                {
                    AddExistingToken(tokens, position.OldElevatedBase, elevatedPath);
                    AddExistingToken(tokens, position.OldOrdinaryBase, ordinaryPath);
                    continue;
                }

                replacedCount++;
                if (!wasPreDetached)
                {
                    // 普通基座与增高基座互相有叠装配合；先拆普通、再拆增高，避免父子配合悬空。
                    RemoveOldModel(position.OldOrdinaryBase, firstFrameAnchors);
                    RemoveOldModel(position.OldElevatedBase, firstFrameAnchors);
                }

                FaceToFaceBaseAssemblyToken elevatedToken = null;
                if (!string.IsNullOrWhiteSpace(elevatedPath))
                {
                    elevatedToken = _operations.InsertAtRoot(
                        position.PositionName,
                        FaceToFaceBaseModelKind.ElevatedBase,
                        elevatedPath,
                        position.TargetPlan.RootCoordinateName);
                    tokens.Add(elevatedToken);
                }

                if (!string.IsNullOrWhiteSpace(ordinaryPath))
                {
                    var ordinaryToken = elevatedToken == null
                        ? _operations.InsertAtRoot(
                            position.PositionName,
                            FaceToFaceBaseModelKind.Base,
                            ordinaryPath,
                            position.TargetPlan.RootCoordinateName)
                        : _operations.InsertBaseOnElevated(
                            position.PositionName,
                            ordinaryPath,
                            elevatedToken);
                    tokens.Add(ordinaryToken);

                    FaceToFaceBaseFirstFrameAnchor firstFrameAnchor;
                    if (firstFrameAnchors.TryGetValue(
                        position.PositionName,
                        out firstFrameAnchor))
                    {
                        _operations.RestoreFirstFrameAnchor(
                            ordinaryToken,
                            firstFrameAnchor);
                    }
                }
            }

            return tokens;
        }

        private void RemoveOldModel(
            FaceToFaceBasePhysicalModelSnapshot oldModel,
            IDictionary<string, FaceToFaceBaseFirstFrameAnchor> firstFrameAnchors)
        {
            if (oldModel == null)
            {
                return;
            }

            if (oldModel.Kind == FaceToFaceBaseModelKind.Base
                && !firstFrameAnchors.ContainsKey(oldModel.PositionName))
            {
                var anchor = _operations.DetachFirstFrameAnchor(oldModel);
                if (anchor != null)
                {
                    if (!string.Equals(
                        oldModel.PositionName,
                        anchor.PositionName,
                        StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"首基架锚点位置与普通基座不一致：基座={oldModel.PositionName}，锚点={anchor.PositionName}。");
                    }

                    firstFrameAnchors.Add(oldModel.PositionName, anchor);
                }
            }

            // SolidWorks 必须先删除该组件参与的定位配合，随后才能可靠移除根组件。
            _operations.DeletePositioningMates(oldModel);
            _operations.RemoveRootComponent(oldModel.ComponentFullName);
        }

        private int CleanupRetiredArtifacts(
            string projectPath,
            FaceToFaceBaseSecondaryDrivePlan drivePlan,
            IEnumerable<string> obsoleteFiles,
            ICollection<string> warnings)
        {
            var baseRoot = Path.Combine(projectPath, "基座");
            IReadOnlyCollection<string> actualReferences;
            try
            {
                actualReferences = _operations.GetReferencedBaseModelPaths();
            }
            catch (Exception exception)
            {
                // 总装已提交后不再回滚；无法证明零引用时宁可保留旧文件，避免误删。
                warnings.Add("提交后读取实际基座引用失败，已保留所有待清理目录和旧文件：" + exception.Message);
                return 0;
            }

            var referencedPaths = new HashSet<string>(
                actualReferences.Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            var closedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var closedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var obsoleteFile in obsoleteFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var safeFile = FaceToFaceBaseTransactionPathGuard.EnsureSafeDescendant(
                        baseRoot,
                        obsoleteFile);
                    if (!referencedPaths.Contains(safeFile) && File.Exists(safeFile))
                    {
                        // 同目录改名时只删除精确旧文件，绝不递归删除仍被新模型使用的目录。
                        CloseModelDocumentOnce(safeFile, closedFiles);
                        File.Delete(safeFile);
                    }
                }
                catch (Exception exception)
                {
                    warnings.Add("旧基座文件清理失败：" + obsoleteFile + "；" + exception.Message);
                }
            }

            var deletedDirectories = 0;
            foreach (var directory in drivePlan.RetirementCandidates
                .Select(x => x.ModelDirectory)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var safeDirectory = FaceToFaceBaseTransactionPathGuard.EnsureSafeDescendant(
                        baseRoot,
                        directory);
                    if (referencedPaths.Any(path => IsPathUnderDirectory(path, safeDirectory)))
                    {
                        // 只要四个位置中仍有一个实际引用落在此目录，就必须保留整个基座文件夹。
                        continue;
                    }

                    // 组件已从总装移除后，再关闭该目录内文档，避免 SolidWorks 文件锁导致清理失败。
                    CloseDirectoryDocumentsOnce(safeDirectory, closedDirectories);
                    Exception cleanupError;
                    if (_directoryCleaner.TryDelete(safeDirectory, out cleanupError))
                    {
                        deletedDirectories++;
                    }
                    else
                    {
                        warnings.Add(
                            "旧基座目录清理失败："
                                + safeDirectory
                                + "；"
                                + (cleanupError?.Message ?? "未知错误"));
                    }
                }
                catch (Exception exception)
                {
                    warnings.Add("旧基座目录清理失败：" + directory + "；" + exception.Message);
                }
            }

            return deletedDirectories;
        }

        private void CloseModelDocumentOnce(string modelPath, ISet<string> closedFiles)
        {
            var fullPath = Path.GetFullPath(modelPath);
            if (!closedFiles.Add(fullPath))
            {
                return;
            }

            // 同目录仍有新模型被总装引用时，只关闭待删的精确旧文件。
            _operations.CloseModelDocument(fullPath);
        }

        private void CloseDirectoryDocumentsOnce(
            string directoryPath,
            ISet<string> closedDirectories)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)
                || !closedDirectories.Add(Path.GetFullPath(directoryPath)))
            {
                return;
            }

            _operations.CloseModelDocuments(directoryPath);
        }

        private void TryClearTransactionArtifacts(ICollection<string> warnings)
        {
            try
            {
                _transaction.Complete();
            }
            catch (Exception exception)
            {
                warnings.Add("基座事务临时文件清理失败：" + exception.Message);
            }
        }

        private static void EnsureFinalState(
            IReadOnlyList<FaceToFaceBasePositionPlan> targetPlans,
            IReadOnlyDictionary<string, PreparedModel> preparedModels,
            FaceToFaceBaseSecondarySnapshot actual)
        {
            var expectedCount = targetPlans.Sum(x => x.HasElevatedBase ? 2 : 1);
            if (actual == null || actual.Models.Count != expectedCount)
            {
                throw new InvalidOperationException(
                    "基座替换后的总装模型数量与当前四位置参数不一致。");
            }

            foreach (var position in targetPlans)
            {
                EnsureFinalModel(
                    actual,
                    position,
                    FaceToFaceBaseModelKind.Base,
                    FindPreparedModel(preparedModels, position.PositionName, FaceToFaceBaseModelKind.Base));
                if (position.HasElevatedBase)
                {
                    EnsureFinalModel(
                        actual,
                        position,
                        FaceToFaceBaseModelKind.ElevatedBase,
                        FindPreparedModel(
                            preparedModels,
                            position.PositionName,
                            FaceToFaceBaseModelKind.ElevatedBase));
                }
            }
        }

        private static void EnsureFinalModel(
            FaceToFaceBaseSecondarySnapshot actual,
            FaceToFaceBasePositionPlan position,
            FaceToFaceBaseModelKind kind,
            PreparedModel expected)
        {
            var model = actual.Models.SingleOrDefault(x =>
                x.Kind == kind
                && string.Equals(x.PositionName, position.PositionName, StringComparison.Ordinal));
            if (model == null
                || !SamePath(model.ModelPath, expected.ModelPath)
                || !model.Identity.Equals(expected.Identity))
            {
                throw new InvalidOperationException(
                    $"基座替换验证失败：{position.PositionName}/{kind} 未指向计划中的最终模型。");
            }
        }

        private static PreparedModel FindPreparedModel(
            IReadOnlyDictionary<string, PreparedModel> preparedModels,
            string positionName,
            FaceToFaceBaseModelKind kind)
        {
            var matches = preparedModels.Values.Where(x =>
                x.Identity != null
                && x.PositionNames.Contains(positionName)
                && x.Kind == kind).ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(
                    $"无法唯一解析目标基座：{positionName}/{kind}。");
            }

            return matches[0];
        }

        private static string ResolvePreparedPath(
            IReadOnlyDictionary<string, PreparedModel> preparedModels,
            string targetKey)
        {
            if (string.IsNullOrWhiteSpace(targetKey))
            {
                return null;
            }

            PreparedModel prepared;
            if (!preparedModels.TryGetValue(targetKey, out prepared))
            {
                throw new InvalidOperationException("未找到已准备的目标基座：" + targetKey);
            }

            return prepared.ModelPath;
        }

        private static void AddExistingToken(
            ICollection<FaceToFaceBaseAssemblyToken> tokens,
            FaceToFaceBasePhysicalModelSnapshot oldModel,
            string finalPath)
        {
            if (oldModel == null)
            {
                return;
            }

            tokens.Add(new FaceToFaceBaseAssemblyToken(
                oldModel.PositionName,
                oldModel.Kind,
                finalPath,
                null,
                oldModel.ComponentFullName));
        }

        private static FaceToFaceBaseModelPlan GetModelPlan(
            FaceToFaceBasePositionPlan position,
            FaceToFaceBaseModelKind kind)
        {
            return kind == FaceToFaceBaseModelKind.Base
                ? position.BaseModel
                : position.ElevatedBaseModel;
        }

        private static FaceToFaceBaseInsulatorPlan GetHostedInsulatorPlan(
            FaceToFaceBasePositionPlan position,
            FaceToFaceBaseModelKind kind)
        {
            return position.Insulator.TargetBaseKind == kind
                ? position.Insulator
                : null;
        }

        private IReadOnlyDictionary<string, FaceToFaceBasePositionPlan> CreateOldPositionPlans(
            FaceToFaceBaseSecondarySnapshot snapshot)
        {
            var plans = _planFactory(snapshot.Parameters)
                .ToDictionary(x => x.PositionName, StringComparer.Ordinal);
            foreach (var source in snapshot.Models)
            {
                if (!plans.TryGetValue(source.PositionName, out var position)
                    || GetModelPlan(position, source.Kind) == null)
                {
                    throw new InvalidOperationException(
                        $"{source.PositionName}/{source.Kind} 缺少对应的旧基座计划，二次驱动已取消。");
                }
            }

            return plans;
        }

        private static FaceToFaceBaseInsulatorPlan FindOldHostedInsulatorPlan(
            IReadOnlyDictionary<string, FaceToFaceBasePositionPlan> oldPositionPlans,
            FaceToFaceBasePhysicalModelSnapshot source)
        {
            return GetHostedInsulatorPlan(oldPositionPlans[source.PositionName], source.Kind);
        }

        private static void RemoveHostedInsulatorIfPresent(
            IFaceToFaceBaseExistingInsulatorDriver insulatorDriver,
            string modelPath,
            FaceToFaceBaseInsulatorPlan oldHostedInsulator)
        {
            if (oldHostedInsulator == null)
            {
                return;
            }

            string componentFullName;
            string ignoredPath;
            if (insulatorDriver.TryGetHostedInsulator(
                modelPath,
                out componentFullName,
                out ignoredPath))
            {
                insulatorDriver.RemoveHostedInsulator(modelPath, componentFullName);
            }
        }

        private static bool CanDriveInsulatorInPlace(
            FaceToFaceBaseSecondarySnapshot snapshot,
            FaceToFaceBasePhysicalModelSnapshot source,
            string insulatorPath)
        {
            if (string.IsNullOrWhiteSpace(insulatorPath))
            {
                return false;
            }

            // 同一基座物理树内的多位置引用可以原地驱动；其它基座树仍引用该绝缘子时改用隔离副本。
            return !snapshot.Models.Any(x =>
                !SamePath(x.ModelPath, source.ModelPath)
                && !string.IsNullOrWhiteSpace(x.InsulatorPath)
                && SamePath(x.InsulatorPath, insulatorPath));
        }

        private static bool InsulatorPlansEqual(
            FaceToFaceBaseInsulatorPlan left,
            FaceToFaceBaseInsulatorPlan right)
        {
            if (left == null || right == null)
            {
                return left == null && right == null;
            }

            return string.Equals(BuildInsulatorKey(left), BuildInsulatorKey(right), StringComparison.Ordinal);
        }

        private static string BuildInsulatorKey(FaceToFaceBaseInsulatorPlan plan)
        {
            return string.Join(
                "|",
                plan.TargetBaseKind,
                plan.PlacementDimensionName,
                Format(plan.PlacementHeight),
                plan.SinglePhaseRootCount.ToString(CultureInfo.InvariantCulture),
                plan.BusbarSpec ?? string.Empty,
                string.Join(
                    ";",
                    plan.GlobalVariables
                        .OrderBy(x => x.Key, StringComparer.Ordinal)
                        .Select(x => x.Key + "=" + Convert.ToString(x.Value, CultureInfo.InvariantCulture))));
        }

        private static string Format(double value)
        {
            return Math.Round(value, 3, MidpointRounding.AwayFromZero)
                .ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static bool SameOptionalPath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
            }

            return SamePath(left, right);
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPathUnderDirectory(string path, string directory)
        {
            var prefix = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private int PositionPriority(string positionName)
        {
            for (var index = 0; index < _positionOrder.Count; index++)
            {
                if (string.Equals(
                    _positionOrder[index],
                    positionName,
                    StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return int.MaxValue;
        }

        private sealed class PreparedModel
        {
            internal PreparedModel(string modelPath, FaceToFaceBaseModelIdentity identity)
            {
                ModelPath = Path.GetFullPath(modelPath);
                Identity = identity;
                PositionNames = new HashSet<string>(StringComparer.Ordinal);
            }

            internal string ModelPath { get; }

            internal FaceToFaceBaseModelIdentity Identity { get; }

            internal FaceToFaceBaseModelKind Kind { get; set; }

            internal ISet<string> PositionNames { get; }
        }

        /// <summary>
        /// 在绝缘子驱动前登记其项目级输出：已有目录先备份，新目录先登记，失败时可完整恢复。
        /// </summary>
        private sealed class TransactionalInsulatorDriver
            : IFaceToFaceBaseExistingInsulatorDriver
        {
            private readonly IFaceToFaceBaseExistingInsulatorDriver _inner;
            private readonly IBaseModelLifecycleTransaction _transaction;
            private readonly string _projectPath;

            internal TransactionalInsulatorDriver(
                IFaceToFaceBaseExistingInsulatorDriver inner,
                IBaseModelLifecycleTransaction transaction,
                string projectPath)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _transaction = transaction
                    ?? throw new ArgumentNullException(nameof(transaction));
                _projectPath = projectPath;
            }

            public string PrepareInsulator(
                string projectPath,
                FaceToFaceBaseInsulatorPlan plan,
                double baseWidth)
            {
                var resolver = _inner as IFaceToFaceBaseInsulatorOutputResolver;
                string plannedDirectory = null;
                if (resolver != null)
                {
                    plannedDirectory = Path.GetFullPath(
                        resolver.ResolveOutputDirectory(_projectPath, plan, baseWidth));
                    if (Directory.Exists(plannedDirectory))
                    {
                        _transaction.BackupModelDirectory(plannedDirectory);
                    }
                    else
                    {
                        _transaction.RegisterNewDirectory(plannedDirectory);
                    }
                }

                var preparedPath = _inner.PrepareInsulator(projectPath, plan, baseWidth);
                if (!string.IsNullOrWhiteSpace(preparedPath)
                    && plannedDirectory != null
                    && !IsPathUnderDirectory(preparedPath, plannedDirectory))
                {
                    throw new InvalidOperationException(
                        "绝缘子实际输出越出事务登记目录：" + preparedPath);
                }

                return preparedPath;
            }

            public void AssembleIntoHostBase(
                string hostBaseAssemblyPath,
                string insulatorAssemblyPath,
                FaceToFaceBaseInsulatorPlan plan)
            {
                _inner.AssembleIntoHostBase(hostBaseAssemblyPath, insulatorAssemblyPath, plan);
            }

            public void DriveExistingInsulator(
                string insulatorAssemblyPath,
                FaceToFaceBaseInsulatorPlan plan,
                double baseWidth)
            {
                _transaction.BackupModelDirectory(insulatorAssemblyPath);
                _inner.DriveExistingInsulator(insulatorAssemblyPath, plan, baseWidth);
            }

            public void WritePlacement(
                string hostBaseAssemblyPath,
                FaceToFaceBaseInsulatorPlan plan)
            {
                _inner.WritePlacement(hostBaseAssemblyPath, plan);
            }

            public bool TryGetHostedInsulator(
                string hostBaseAssemblyPath,
                out string componentFullName,
                out string modelPath)
            {
                return _inner.TryGetHostedInsulator(
                    hostBaseAssemblyPath,
                    out componentFullName,
                    out modelPath);
            }

            public void RemoveHostedInsulator(
                string hostBaseAssemblyPath,
                string componentFullName)
            {
                _inner.RemoveHostedInsulator(hostBaseAssemblyPath, componentFullName);
            }
        }

        /// <summary>
        /// 把驱动器内部的“查看/提交”改为本事务内预留；底层生成器只在总装保存成功后统一提交。
        /// </summary>
        private sealed class DeferredDrawingNumberGenerator : IDrawingNumberGenerator
        {
            private readonly IDrawingNumberGenerator _inner;
            private readonly List<string> _reserved = new List<string>();
            private string _baseNumber;

            internal DeferredDrawingNumberGenerator(IDrawingNumberGenerator inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public string PeekNextDrawingNumber()
            {
                if (_baseNumber == null)
                {
                    _baseNumber = _inner.PeekNextDrawingNumber();
                }

                return DrawingNumberHelper.Offset(_baseNumber, _reserved.Count);
            }

            public string PeekNextFileNameSuffix()
            {
                return DrawingNumberHelper.ToFileNameSuffix(PeekNextDrawingNumber());
            }

            public void CommitNext(string drawingNumber)
            {
                var expected = PeekNextDrawingNumber();
                if (!string.Equals(expected, drawingNumber, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"事务内提交图号与当前预留图号不一致：当前={expected}，提交={drawingNumber}");
                }

                _reserved.Add(expected);
            }

            internal string ReserveNextFileNameSuffix()
            {
                var number = PeekNextDrawingNumber();
                var suffix = DrawingNumberHelper.ToFileNameSuffix(number);
                CommitNext(number);
                return suffix;
            }

            internal void CommitAll()
            {
                foreach (var number in _reserved)
                {
                    _inner.CommitNext(number);
                }
            }
        }
    }
}
