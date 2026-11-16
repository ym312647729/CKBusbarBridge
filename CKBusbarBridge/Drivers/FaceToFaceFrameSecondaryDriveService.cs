using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 编排面对面基架的局部二次生成，并以总装映射写入与保存作为提交点。
    /// </summary>
    internal sealed class FaceToFaceFrameSecondaryDriveService
    {
        private const string FrameRootDirectoryName = "基架";
        private const string FaceToWallRootCoordinateSuffix = "安装坐标系";
        private readonly IFaceToFaceFrameModelPreparationService _modelPreparationService;
        private readonly IFaceToFaceFrameExistingModelUpdater _existingModelUpdater;
        private readonly IFaceToFaceFrameSecondaryAssemblyOperations _operations;
        private readonly IFaceToFaceBaseDirectoryCleaner _directoryCleaner;

        internal FaceToFaceFrameSecondaryDriveService(
            IFaceToFaceFrameModelPreparationService modelPreparationService,
            IFaceToFaceFrameExistingModelUpdater existingModelUpdater,
            IFaceToFaceFrameSecondaryAssemblyOperations operations,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner)
        {
            _modelPreparationService = modelPreparationService
                ?? throw new ArgumentNullException(nameof(modelPreparationService));
            _existingModelUpdater = existingModelUpdater
                ?? throw new ArgumentNullException(nameof(existingModelUpdater));
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            _directoryCleaner = directoryCleaner
                ?? throw new ArgumentNullException(nameof(directoryCleaner));
        }

        internal FaceToFaceFrameSecondaryDriveResult Update(
            string projectPath,
            FaceToFaceFrameParameterSnapshot snapshot,
            IReadOnlyList<FaceToFaceFramePositionPlan> currentPlans,
            string ordinaryBasePositionName,
            IDrawingNumberGenerator drawingNumbers,
            FaceToFaceFrameInsulatorParameters insulatorParameters,
            Action<FaceToFaceFrameParameterSnapshot> prepareCommitMetadata = null,
            Action<string, int, int> reportProgress = null,
            bool deferFinalCleanup = false,
            bool refreshExistingInsulators = false,
            IReadOnlyCollection<string> dirtyModelSources = null, bool forceInstallationRepair = false,
            bool useInstanceIdentity = false)
        {
            // 差异规划为纯计算，必须在任何模型或磁盘操作之前完成。
            var plan = FaceToFaceFrameSecondaryDrivePlanner.Create(snapshot, currentPlans, useInstanceIdentity);
            // 母线桥入口的N、相间距和本体高属于独立输入，不包含在旧基架尺寸快照中。
            if (refreshExistingInsulators)
                plan = new FaceToFaceFrameSecondaryDrivePlan(plan.ModelChanges.Select(change =>
                    change.Kind == FaceToFaceFrameModelChangeKind.Keep && change.TargetPlan.InsulatorCount > 0
                        ? new FaceToFaceFrameModelChange(FaceToFaceFrameModelChangeKind.DriveExisting,
                            change.SourceName, change.ExistingModel, change.TargetPlan) : change),
                    plan.PositionChanges, plan.TargetPlans);
            if (dirtyModelSources != null)
            {
                var dirty = new HashSet<string>(dirtyModelSources, StringComparer.OrdinalIgnoreCase);
                plan = new FaceToFaceFrameSecondaryDrivePlan(plan.ModelChanges.Select(change =>
                    change.Kind == FaceToFaceFrameModelChangeKind.Keep && dirty.Contains(change.SourceName)
                        ? new FaceToFaceFrameModelChange(FaceToFaceFrameModelChangeKind.DriveExisting,
                            change.SourceName, change.ExistingModel, change.TargetPlan) : change),
                    plan.PositionChanges, plan.TargetPlans);
            }
            if (plan.IsEmpty && !forceInstallationRepair)
            {
                return new FaceToFaceFrameSecondaryDriveResult(
                    false,
                    0,
                    0,
                    0,
                    0,
                    Array.Empty<string>(),
                    snapshot);
            }

            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("项目路径不能为空。", nameof(projectPath));
            }

            if (string.IsNullOrWhiteSpace(ordinaryBasePositionName))
            {
                throw new ArgumentException("普通基座定位名称不能为空。", nameof(ordinaryBasePositionName));
            }

            if (drawingNumbers == null)
            {
                throw new ArgumentNullException(nameof(drawingNumbers));
            }

            if (plan.ModelChanges.Any(change =>
                    (change.Kind == FaceToFaceFrameModelChangeKind.Create
                        || change.Kind == FaceToFaceFrameModelChangeKind.Recreate
                        || change.Kind == FaceToFaceFrameModelChangeKind.DriveExisting)
                    && change.TargetPlan.InsulatorCount > 0)
                && insulatorParameters == null)
            {
                throw new ArgumentNullException(
                    nameof(insulatorParameters),
                    "存在需要生成或对账绝缘子的基架，但未提供绝缘子驱动参数。");
            }

            var fullProjectPath = Path.GetFullPath(projectPath);
            if (!Directory.Exists(fullProjectPath))
            {
                throw new DirectoryNotFoundException("项目目录不存在：" + fullProjectPath);
            }

            var liveSnapshot = _operations.CaptureCurrentSnapshot(
                fullProjectPath,
                ordinaryBasePositionName);
            if (!SnapshotsEqual(snapshot, liveSnapshot))
            {
                throw new InvalidOperationException(
                    "当前总装基架状态与打开界面时的快照不一致，请重新反读后再生成。");
            }

            var frameRoot = Path.Combine(fullProjectPath, FrameRootDirectoryName);
            plan = ResolveStoredModels(plan, frameRoot);
            var targetDirectoryBySource = ValidateTargetModelDirectories(
                plan.ModelChanges,
                frameRoot);
            foreach (var change in plan.ModelChanges.Where(change =>
                change.Kind == FaceToFaceFrameModelChangeKind.Create
                || change.Kind == FaceToFaceFrameModelChangeKind.Recreate))
            {
                _modelPreparationService.ResolveAndValidateTemplateAssemblyPath(change.TargetPlan);
            }
            // 在事务开始前一次性整理业务预拆集合。这里不再扫描模板文档标题，
            // 文档释放统一由“保存并关闭总装”承担，预拆范围只由实际引用变化决定。
            var batch = BuildExecutionBatch(snapshot, plan);
            var requiresClosedModelPhase = plan.ModelChanges.Any(change =>
                change.Kind == FaceToFaceFrameModelChangeKind.DriveExisting
                || change.Kind == FaceToFaceFrameModelChangeKind.Create
                || change.Kind == FaceToFaceFrameModelChangeKind.Recreate);
            var changedModelCount = plan.ModelChanges.Count(change =>
                change.Kind == FaceToFaceFrameModelChangeKind.DriveExisting
                || change.Kind == FaceToFaceFrameModelChangeKind.Create
                || change.Kind == FaceToFaceFrameModelChangeKind.Recreate);
            var totalSteps = 5 + changedModelCount + plan.TargetPlans.Count
                + (requiresClosedModelPhase ? 1 : 0);
            var completedSteps = 0;
            var completedModels = 0;
            var baselineSaved = false;
            var committed = false;
            var drivenModelCount = 0;
            var createdModelCount = 0;
            var changedPositionCount = plan.PositionChanges.Count(change =>
                change.Kind != FaceToFaceFramePositionChangeKind.Keep);
            var warnings = new List<string>();
            var modelPaths = snapshot.Models.Values.ToDictionary(
                model => model.SourceName,
                model => model.ModelPath,
                StringComparer.OrdinalIgnoreCase);
            FaceToFaceFrameParameterSnapshot committedSnapshot = null;

            try
            {
                reportProgress?.Invoke("正在保存更新前基线并备份模型", completedSteps, totalSteps);
                _operations.SaveBaseline();
                baselineSaved = true;
                var originalMapping = _operations.CaptureMapping();
                var preparedInsulators = new Dictionary<string, string>(StringComparer.Ordinal);

                // 所有会被改写或删除的已有目录先完成备份。备份本身不改变总装引用，
                // 但必须早于预拆后的任何模型驱动，才能保证后续任一步失败都有完整恢复源。
                foreach (var modelChange in plan.ModelChanges.Where(change =>
                    change.Kind == FaceToFaceFrameModelChangeKind.DriveExisting
                    || change.Kind == FaceToFaceFrameModelChangeKind.Recreate))
                {
                    _operations.BackupModelDirectory(modelChange.ExistingModel);
                }

                // 第一阶段只移除组件，不驱动、不生成也不插入。位置引用变化、删除位置和
                // 同 SourceName 模板身份迁移的并集已经在 batch 中按组件实例去重。
                completedSteps++;
                reportProgress?.Invoke("正在预拆变更位置并释放模型文档", completedSteps, totalSteps);
                var detachedComponentIds = DetachPositions(batch);
                if (batch.Detachments.Count > 0)
                {
                    _operations.RebuildAfterDetach();
                }

                if (requiresClosedModelPhase)
                {
                    // 预拆状态会被保存到磁盘，因此 SaveBaseline 必须已经保留完整总装文件副本。
                    // 关闭总装统一卸载仍保留在装配体内的原位驱动实例，无需为了文档占用扩大预拆范围。
                    _operations.SaveDetachedAssemblyAndClose();
                }

                // Recreate 保持 SourceName 不变，因此目标目录正是旧目录。只有在整组组件零引用、
                // 文档关闭且备份成功后，才允许删除旧树并把该路径登记为本次新建目录。
                foreach (var recreate in plan.ModelChanges.Where(change =>
                    change.Kind == FaceToFaceFrameModelChangeKind.Recreate))
                {
                    _operations.RegisterRecreatedModelDirectory(
                        recreate.ExistingModel,
                        targetDirectoryBySource[recreate.SourceName]);
                }

                // 第二阶段集中完成全部物理模型。已有模型原位驱动不要求扩大预拆范围；
                // 总装关闭后，Create/Recreate 也可直接执行模板复制和模型驱动。
                completedSteps++;
                foreach (var modelChange in plan.ModelChanges)
                {
                    var changesModel = modelChange.Kind == FaceToFaceFrameModelChangeKind.DriveExisting
                        || modelChange.Kind == FaceToFaceFrameModelChangeKind.Create
                        || modelChange.Kind == FaceToFaceFrameModelChangeKind.Recreate;
                    if (changesModel)
                        reportProgress?.Invoke($"正在更新基架及绝缘子 {completedModels + 1}/{changedModelCount}：{modelChange.SourceName}", completedSteps, totalSteps);
                    switch (modelChange.Kind)
                    {
                        case FaceToFaceFrameModelChangeKind.DriveExisting:
                            // 外形和内部绝缘子必须使用同一目标计划完成对账，否则最终物理回读
                            // 会把旧绝缘子数量/定位判定为位置参数不一致。
                            _existingModelUpdater.UpdateExistingModel(
                                fullProjectPath,
                                modelChange.ExistingModel.ModelPath,
                                modelChange.TargetPlan,
                                insulatorParameters);
                            modelPaths[modelChange.SourceName] = modelChange.ExistingModel.ModelPath;
                            drivenModelCount++;
                            break;

                        case FaceToFaceFrameModelChangeKind.Create:
                            // 在 PackAndGo 前登记确切目标，失败回滚只能删除本次明确创建的目录。
                            _operations.RegisterNewModelDirectory(
                                targetDirectoryBySource[modelChange.SourceName]);
                            goto case FaceToFaceFrameModelChangeKind.Recreate;

                        case FaceToFaceFrameModelChangeKind.Recreate:
                            // 在 PackAndGo 前登记确切目标，失败回滚只能删除本次明确创建的目录。
                            var createdPath = _modelPreparationService.PrepareNewModel(
                                fullProjectPath,
                                frameRoot,
                                modelChange.TargetPlan,
                                drawingNumbers,
                                insulatorParameters,
                                preparedInsulators);
                            modelPaths[modelChange.SourceName] = createdPath;
                            createdModelCount++;
                            break;
                    }
                    if (changesModel)
                    {
                        completedSteps++;
                        completedModels++;
                    }
                }

                if (requiresClosedModelPhase)
                {
                    reportProgress?.Invoke("正在关闭已更新模型并重新打开总装", completedSteps, totalSteps);
                    // 驱动器通常会自行关闭输出文档；这里再按确切模型目录统一复核，
                    // 避免新模型仍以独立文档驻留时重新打开总装并复用旧的内存引用。
                    CloseChangedModelDocuments(plan.ModelChanges, modelPaths);

                    // 总装重新打开后必须先完整重建，再允许第一次 InsertPosition。
                    // 这样未预拆的 DriveExisting 实例以及引用了已修改子件的上层组件都会刷新。
                    _operations.ReopenAssemblyAndForceRebuild();
                    completedSteps++;
                }

                // 第三个阶段才允许第一次插入。预拆位置和新增位置统一走 InsertPosition；
                // 未预拆位置保留原组件实例，但同样进入最终令牌集合参与整链重配。
                var completedBeforePositions = completedSteps;
                var finalTokens = AssembleFinalPositions(
                    plan,
                    detachedComponentIds,
                    modelPaths,
                    (detail, current, total) => reportProgress?.Invoke(
                        detail, completedBeforePositions + current, totalSteps));
                completedSteps += plan.TargetPlans.Count;
                reportProgress?.Invoke("正在重新配合并校验最终基架链", completedSteps, totalSteps);
                // 面对墙模型反读从总装根“柜前/柜后安装坐标系”定位普通基座，
                // 但模型对照映射仍以“柜前/柜后”为位置键；重新配合时必须使用映射键。
                _operations.RemateFinalChain(
                    finalTokens,
                    ResolveAssemblyBasePositionName(ordinaryBasePositionName));
                // 用最终组件实例先生成候选映射。侧对侧严格反读必须同时验证物理模板属性
                // 与这份待提交映射，不能继续拿旧映射误判一次合法的模板迁移。
                var finalMapping = FaceToFaceFrameModelMappingHelper.ReplaceFrames(
                    originalMapping,
                    finalTokens);
                committedSnapshot = _operations.ValidateFinalChain(
                    fullProjectPath,
                    ordinaryBasePositionName,
                    finalMapping);
                ValidateFinalSnapshot(committedSnapshot, plan.TargetPlans, modelPaths);

                // 图号等伴随元数据必须在同一次总装保存前写入；
                // 回调失败仍属于提交前失败，会触发总装和文件回滚。
                completedSteps++;
                reportProgress?.Invoke("正在写回图号、模型映射并保存总装", completedSteps, totalSteps);
                prepareCommitMetadata?.Invoke(committedSnapshot);
                _operations.WriteMappingAndSave(finalMapping);
                committed = true;
                completedSteps++;
                reportProgress?.Invoke("正在清理零引用目录及事务备份", completedSteps, totalSteps);

                // 提交后再读总装真实路径做引用计数；只要仍有一个位置引用，目录必须保留。
                var deletedDirectoryCount = deferFinalCleanup ? 0 : TryCleanupRetiredDirectoriesAfterCommit(
                    plan.ModelChanges,
                    frameRoot,
                    warnings);
                if (!deferFinalCleanup) TryClearTransactionBackups(warnings);
                reportProgress?.Invoke("基架更新事务已完成", ++completedSteps, totalSteps);

                return new FaceToFaceFrameSecondaryDriveResult(
                    true,
                    drivenModelCount,
                    createdModelCount,
                    changedPositionCount,
                    deletedDirectoryCount,
                    warnings,
                    committedSnapshot);
            }
            catch (Exception primaryException) when (!(primaryException is FrameDriveInspectionRequiredException))
            {
                if (baselineSaved && !committed)
                {
                    reportProgress?.Invoke("基架更新失败，正在恢复总装及模型", completedSteps, totalSteps);
                    TryRollback(primaryException);
                }

                throw;
            }
        }

        private FaceToFaceFrameSecondaryDrivePlan ResolveStoredModels(
            FaceToFaceFrameSecondaryDrivePlan plan, string frameRoot)
        {
            var changes = new List<FaceToFaceFrameModelChange>();
            foreach (var change in plan.ModelChanges)
            {
                if (change.Kind != FaceToFaceFrameModelChangeKind.Create)
                {
                    changes.Add(change);
                    continue;
                }

                var directory = Path.GetFullPath(Path.Combine(frameRoot, change.TargetPlan.FolderName));
                if (!IsSafeRetiredDirectory(frameRoot, directory))
                    throw new InvalidOperationException("基架目标目录超出项目范围：" + directory);
                if (!Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    changes.Add(change);
                    continue;
                }

                var candidates = Directory.EnumerateFiles(directory)
                    .Where(path => FaceToFaceFramePathClassifier.TryGetStoredTemplateIdentity(path, out _))
                    .ToArray();
                if (candidates.Length != 1)
                    throw new InvalidOperationException("基架目录中无法唯一确定旧主模型，已保留目录：" + directory);
                var reader = _operations as IFaceToFaceFrameStoredModelReader
                    ?? throw new InvalidOperationException("当前操作器不支持读取已有基架模板身份。");
                var identity = reader.ReadStoredTemplateIdentity(candidates[0]);
                var kind = identity == FrameTemplateIdentity.FaceToWallLarge1 ? FaceToWallFrameTemplateKind.Large1
                    : identity == FrameTemplateIdentity.FaceToWallLarge2 ? FaceToWallFrameTemplateKind.Large2
                    : identity == FrameTemplateIdentity.FaceToFaceLargeOffsetCorner028
                        ? FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner : FaceToWallFrameTemplateKind.Regular;
                // 未装配模型尚未读取参数，不能判为 Keep；同模板必须全量驱动到目标参数。
                var existing = new FaceToFaceFrameModelSnapshot(change.SourceName, candidates[0],
                    0, 0, 0, Array.Empty<double>(), kind, templateIdentity: identity);
                changes.Add(new FaceToFaceFrameModelChange(
                    identity == change.TargetPlan.TemplateIdentity ? FaceToFaceFrameModelChangeKind.DriveExisting
                        : FaceToFaceFrameModelChangeKind.Recreate,
                    change.SourceName, existing, change.TargetPlan));
            }

            return new FaceToFaceFrameSecondaryDrivePlan(changes, plan.PositionChanges, plan.TargetPlans);
        }

        private static IReadOnlyDictionary<string, string> ValidateTargetModelDirectories(
            IEnumerable<FaceToFaceFrameModelChange> modelChanges,
            string frameRoot)
        {
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in modelChanges.Where(item =>
                item.Kind == FaceToFaceFrameModelChangeKind.Create
                || item.Kind == FaceToFaceFrameModelChangeKind.Recreate))
            {
                var directory = Path.GetFullPath(Path.Combine(
                    frameRoot,
                    change.TargetPlan.FolderName));
                if (!IsSafeRetiredDirectory(frameRoot, directory))
                {
                    throw new InvalidOperationException(
                        $"新增基架“{change.SourceName}”的目标目录超出项目基架根目录：{directory}");
                }

                if (change.Kind == FaceToFaceFrameModelChangeKind.Create
                    && (File.Exists(directory) || (Directory.Exists(directory)
                        && Directory.EnumerateFileSystemEntries(directory).Any())))
                {
                    throw new IOException(
                        $"新增基架“{change.SourceName}”的目标目录已存在，拒绝覆盖：{directory}");
                }

                if (change.Kind == FaceToFaceFrameModelChangeKind.Recreate)
                {
                    var existingDirectory = Path.GetFullPath(
                        Path.GetDirectoryName(change.ExistingModel.ModelPath));
                    if (!string.Equals(
                            existingDirectory,
                            directory,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"模板迁移基架“{change.SourceName}”必须在原 SourceName 目录重建："
                            + $"原目录={existingDirectory}；目标目录={directory}");
                    }

                    if (!Directory.Exists(existingDirectory))
                    {
                        throw new DirectoryNotFoundException(
                            "模板迁移旧基架目录不存在：" + existingDirectory);
                    }
                }

                paths.Add(change.SourceName, directory);
            }

            return paths;
        }

        private FaceToFaceFrameSecondaryDriveBatch BuildExecutionBatch(
            FaceToFaceFrameParameterSnapshot snapshot,
            FaceToFaceFrameSecondaryDrivePlan plan)
        {
            var reasonsByComponent = new Dictionary<
                string,
                Tuple<FaceToFaceFramePositionSnapshot, FaceToFaceFrameDetachReason>>(
                    StringComparer.OrdinalIgnoreCase);

            void AddDetachment(
                FaceToFaceFramePositionSnapshot position,
                FaceToFaceFrameDetachReason reason)
            {
                if (position == null)
                {
                    return;
                }

                if (reasonsByComponent.TryGetValue(
                        position.ComponentInstanceId,
                        out var existing))
                {
                    reasonsByComponent[position.ComponentInstanceId] = Tuple.Create(
                        existing.Item1,
                        existing.Item2 | reason);
                }
                else
                {
                    reasonsByComponent.Add(
                        position.ComponentInstanceId,
                        Tuple.Create(position, reason));
                }
            }

            foreach (var change in plan.PositionChanges)
            {
                if (change.Kind == FaceToFaceFramePositionChangeKind.Replace)
                {
                    AddDetachment(
                        change.ExistingPosition,
                        FaceToFaceFrameDetachReason.PositionReferenceChanged);
                }
                else if (change.Kind == FaceToFaceFramePositionChangeKind.Remove)
                {
                    AddDetachment(
                        change.ExistingPosition,
                        FaceToFaceFrameDetachReason.PositionRemoved);
                }
            }

            var recreatedSources = new HashSet<string>(
                plan.ModelChanges
                    .Where(change => change.Kind == FaceToFaceFrameModelChangeKind.Recreate)
                    .Select(change => change.SourceName),
                StringComparer.OrdinalIgnoreCase);
            foreach (var position in snapshot.Positions.Where(position =>
                recreatedSources.Contains(position.SourceName)))
            {
                AddDetachment(position, FaceToFaceFrameDetachReason.SourceRecreated);
            }

            var detachments = reasonsByComponent.Values.Select(item =>
                new FaceToFaceFrameDetachPlan(item.Item1, item.Item2));
            return new FaceToFaceFrameSecondaryDriveBatch(
                plan,
                detachments);
        }

        private ISet<string> DetachPositions(FaceToFaceFrameSecondaryDriveBatch batch)
        {
            var detached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 从尾部向前删除可避免 SolidWorks 在删除前部实例后重排后续同名组件序号，
            // 组件实例 ID 虽然是主要定位依据，倒序仍能降低第三方配合引用漂移的风险。
            foreach (var item in batch.Detachments.OrderByDescending(detachment =>
                detachment.Position.PositionIndex))
            {
                _operations.RemovePosition(item.Position);
                detached.Add(item.Position.ComponentInstanceId);
            }

            return detached;
        }

        private void CloseChangedModelDocuments(
            IEnumerable<FaceToFaceFrameModelChange> modelChanges,
            IReadOnlyDictionary<string, string> modelPaths)
        {
            var changedSources = new HashSet<string>(
                (modelChanges ?? Enumerable.Empty<FaceToFaceFrameModelChange>())
                    .Where(change =>
                        change.Kind == FaceToFaceFrameModelChangeKind.DriveExisting
                        || change.Kind == FaceToFaceFrameModelChangeKind.Create
                        || change.Kind == FaceToFaceFrameModelChangeKind.Recreate)
                    .Select(change => change.SourceName),
                StringComparer.OrdinalIgnoreCase);
            foreach (var directory in changedSources
                .Select(source => modelPaths[source])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetDirectoryName(Path.GetFullPath(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var close = _operations.CloseDocumentsUnderDirectory(directory);
                if (close == null || !close.IsConfirmed)
                {
                    throw new InvalidOperationException(
                        "重新打开总装前无法确认本次处理的基架文档已经关闭："
                        + directory + "；"
                        + (close?.Diagnostic ?? "未返回关闭确认"));
                }
            }
        }

        private static string ResolveAssemblyBasePositionName(string readBasePositionName)
        {
            var normalized = readBasePositionName.Trim();
            return normalized.EndsWith(
                    FaceToWallRootCoordinateSuffix,
                    StringComparison.Ordinal)
                ? normalized.Substring(
                    0,
                    normalized.Length - FaceToWallRootCoordinateSuffix.Length)
                : normalized;
        }

        private IReadOnlyList<FaceToFaceFrameAssemblyToken> AssembleFinalPositions(
            FaceToFaceFrameSecondaryDrivePlan plan,
            ISet<string> detachedComponentIds,
            IReadOnlyDictionary<string, string> modelPaths,
            Action<string, int, int> reportProgress)
        {
            var finalTokens = new List<FaceToFaceFrameAssemblyToken>();
            var changeByTargetPosition = plan.PositionChanges
                .Where(change => change.Kind != FaceToFaceFramePositionChangeKind.Remove)
                .ToDictionary(change => change.TargetPlan.PositionIndex);
            foreach (var target in plan.TargetPlans.OrderBy(item => item.PositionIndex))
            {
                reportProgress?.Invoke($"正在核对并装配基架位置 {finalTokens.Count + 1}/{plan.TargetPlans.Count}：{target.SourceName}", finalTokens.Count, plan.TargetPlans.Count);
                var positionChange = changeByTargetPosition[target.PositionIndex];
                var existing = positionChange.ExistingPosition;
                var wasDetached = existing != null
                    && detachedComponentIds.Contains(existing.ComponentInstanceId);
                if (existing == null || wasDetached)
                {
                    finalTokens.Add(_operations.InsertPosition(
                        target,
                        modelPaths[target.SourceName]));
                    continue;
                }

                // 未预拆位置继续使用原组件实例。即使其 SourceName 对应 DriveExisting，
                // 物理模型也已在上一阶段原位完成驱动，无需为了同步参数重复替换组件。
                finalTokens.Add(new FaceToFaceFrameAssemblyToken(
                    target,
                    modelPaths[target.SourceName],
                    null,
                    existing.ComponentInstanceId));
            }

            return finalTokens.AsReadOnly();
        }

        private int CleanupRetiredDirectories(
            IEnumerable<FaceToFaceFrameModelChange> modelChanges,
            IEnumerable<string> referencedPaths,
            string frameRoot,
            ICollection<string> warnings)
        {
            var references = referencedPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .ToArray();
            var deleted = 0;
            foreach (var retired in modelChanges.Where(change =>
                change.Kind == FaceToFaceFrameModelChangeKind.RetireCandidate))
            {
                var directory = Path.GetDirectoryName(
                    Path.GetFullPath(retired.ExistingModel.ModelPath));
                if (!IsSafeRetiredDirectory(frameRoot, directory))
                {
                    // 即使快照来源异常，也不能让清理动作越过当前项目的基架根目录。
                    warnings.Add(
                        $"基架“{retired.SourceName}”的目录不在当前项目基架根目录内，已拒绝删除：{directory}");
                    continue;
                }

                if (references.Any(path => FaceToFaceFramePathClassifier.IsPathUnderDirectory(
                    directory,
                    path)))
                {
                    continue;
                }

                var close = _operations.CloseDocumentsUnderDirectory(directory);
                if (close == null || !close.IsConfirmed)
                {
                    warnings.Add(
                        $"基架“{retired.SourceName}”文档关闭未确认，已保留目录：{directory}；"
                        + (close?.Diagnostic ?? "未返回关闭确认"));
                    continue;
                }

                if (_directoryCleaner.TryDelete(directory, out var cleanupError))
                {
                    deleted++;
                }
                else
                {
                    warnings.Add(
                        $"基架“{retired.SourceName}”已零引用，但删除目录失败：{directory}；"
                        + (cleanupError?.Message ?? "未知错误"));
                }
            }

            return deleted;
        }

        private int TryCleanupRetiredDirectoriesAfterCommit(
            IEnumerable<FaceToFaceFrameModelChange> modelChanges,
            string frameRoot,
            ICollection<string> warnings)
        {
            try
            {
                var referencedPaths = _operations.GetReferencedFrameModelPaths()
                    ?? Array.Empty<string>();
                return CleanupRetiredDirectories(
                    modelChanges,
                    referencedPaths,
                    frameRoot,
                    warnings);
            }
            catch (Exception exception)
            {
                // 总装已经提交后，任何引用枚举或清理异常都只能降级为警告；
                // 无法确认零引用时保守保留退休目录，不能把已保存结果报告成提交失败。
                warnings.Add(
                    "基架修改已保存，但退休目录引用核对或清理失败，未确认目录已保留："
                    + exception.Message);
                return 0;
            }
        }

        private static bool IsSafeRetiredDirectory(string frameRoot, string directory)
        {
            try
            {
                var root = Path.GetFullPath(frameRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var candidate = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
                    && FaceToFaceFramePathClassifier.IsPathUnderDirectory(root, candidate)
                    && !Path.GetFileName(candidate).StartsWith(
                        ".__secondary_transaction_",
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void TryRollback(Exception primaryException)
        {
            try
            {
                // 事务可能在预拆后、物理模型处理中或重新装配后失败。无论处于哪一阶段，
                // 都先丢弃当前内存总装，确保后续磁盘恢复不会与仍驻留的引用发生竞争。
                _operations.CloseTotalAssemblyWithoutSave();
            }
            catch (Exception closeException)
            {
                FaceToFaceFrameCleanupDiagnostics.AppendData(
                    primaryException,
                    FaceToFaceFrameCleanupDiagnostics.RollbackWarningDataKey,
                    "回滚前关闭总装配体失败：" + closeException.Message);
                // 无法证明总装已关闭时禁止覆盖模型树或总装文件；保留事务目录供人工恢复。
                return;
            }

            try
            {
                // 总装关闭后一次性恢复所有已修改模型目录、删除本次新增目录，
                // 并用 SaveBaseline 保存的原始文件副本覆盖预拆状态的总装文件。
                _operations.RestoreBackupsAndRemoveNewDirectories();
            }
            catch (Exception restoreException)
            {
                FaceToFaceFrameCleanupDiagnostics.AppendData(
                    primaryException,
                    FaceToFaceFrameCleanupDiagnostics.RollbackWarningDataKey,
                    "恢复基架二次生成文件失败：" + restoreException.Message);
                return;
            }

            try
            {
                // 恢复完成后重新打开原总装并立即完整重建，使内存状态与恢复后的子件一致。
                _operations.ReopenAssemblyAndForceRebuild();
            }
            catch (Exception reopenException)
            {
                FaceToFaceFrameCleanupDiagnostics.AppendData(
                    primaryException,
                    FaceToFaceFrameCleanupDiagnostics.RollbackWarningDataKey,
                    "恢复后重新打开总装配体失败：" + reopenException.Message);
                return;
            }

            try
            {
                // 只有“文件完整恢复 + 总装重新打开并重建”全部成功，事务备份才失去恢复价值。
                _operations.ClearTransactionBackups();
            }
            catch (Exception cleanupException)
            {
                FaceToFaceFrameCleanupDiagnostics.AppendData(
                    primaryException,
                    FaceToFaceFrameCleanupDiagnostics.RollbackWarningDataKey,
                    "回滚成功但清理事务备份失败：" + cleanupException.Message);
            }
        }

        private void TryClearTransactionBackups(ICollection<string> warnings)
        {
            try
            {
                _operations.ClearTransactionBackups();
            }
            catch (Exception exception)
            {
                warnings.Add("清理基架二次生成事务备份失败：" + exception.Message);
            }
        }

        private static bool SnapshotsEqual(
            FaceToFaceFrameParameterSnapshot expected,
            FaceToFaceFrameParameterSnapshot actual)
        {
            if (expected == null
                || actual == null
                || expected.Positions.Count != actual.Positions.Count
                || expected.Models.Count != actual.Models.Count)
            {
                return false;
            }

            for (var index = 0; index < expected.Positions.Count; index++)
            {
                var first = expected.Positions[index];
                var second = actual.Positions[index];
                if (first.PositionIndex != second.PositionIndex
                    || !SnapshotRoutesEqual(first, second)
                    || !string.Equals(first.ComponentInstanceId, second.ComponentInstanceId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(first.SourceName, second.SourceName, StringComparison.OrdinalIgnoreCase)
                    || !PathsEqual(first.ModelPath, second.ModelPath))
                {
                    return false;
                }
            }

            foreach (var model in expected.Models.Values)
            {
                if (!actual.Models.TryGetValue(model.SourceName, out var other)
                    || !PathsEqual(model.ModelPath, other.ModelPath)
                    || !model.HasSameParameters(other))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SnapshotRoutesEqual(
            FaceToFaceFramePositionSnapshot first,
            FaceToFaceFramePositionSnapshot second)
        {
            if (!string.IsNullOrWhiteSpace(first.InletCoordinateName)
                || !string.IsNullOrWhiteSpace(second.InletCoordinateName))
            {
                return string.Equals(
                        first.InletCoordinateName,
                        second.InletCoordinateName,
                        StringComparison.Ordinal)
                    && string.Equals(
                        first.OutletCoordinateName,
                        second.OutletCoordinateName,
                        StringComparison.Ordinal);
            }

            // 兼容未携带真实坐标系的普通/小偏移旧快照。
            if (first.Model.FrameTemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner ||
                second.Model.FrameTemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner) return false;
            return first.IsReversed == second.IsReversed;
        }

        private static void ValidateFinalSnapshot(
            FaceToFaceFrameParameterSnapshot finalSnapshot,
            IReadOnlyList<FaceToFaceFramePositionPlan> targetPlans,
            IReadOnlyDictionary<string, string> modelPaths)
        {
            if (finalSnapshot == null || finalSnapshot.Positions.Count != targetPlans.Count)
            {
                throw new InvalidOperationException("二次生成后的基架数量与界面目标不一致。");
            }

            for (var index = 0; index < targetPlans.Count; index++)
            {
                var actual = finalSnapshot.Positions[index];
                var target = targetPlans[index];
                var differences = DescribeFinalPositionDifferences(
                    actual,
                    target,
                    modelPaths);
                if (differences.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"二次生成后{target.PositionKey}与目标不一致："
                        + string.Join("；", differences) + "。");
                }
            }
        }

        private static IReadOnlyList<string> DescribeFinalPositionDifferences(
            FaceToFaceFramePositionSnapshot actual,
            FaceToFaceFramePositionPlan target,
            IReadOnlyDictionary<string, string> modelPaths)
        {
            var differences = new List<string>();
            if (actual.PositionIndex != target.PositionIndex)
            {
                differences.Add(
                    $"位置序号[期望={target.PositionIndex}，实际={actual.PositionIndex}]");
            }

            if (!ActualRouteMatchesTarget(actual, target))
            {
                differences.Add(
                    $"进出坐标系[期望={FormatRoute(target.InletCoordinateName, target.OutletCoordinateName)}，"
                    + $"实际={FormatRoute(actual.InletCoordinateName, actual.OutletCoordinateName)}]");
            }

            if (!string.Equals(
                    actual.SourceName,
                    target.SourceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                differences.Add(
                    $"SourceName[期望={target.SourceName}，实际={actual.SourceName}]");
            }

            if (!modelPaths.TryGetValue(target.SourceName, out var expectedPath))
            {
                differences.Add($"目标模型路径缺失[SourceName={target.SourceName}]");
            }
            else if (!PathsEqual(actual.ModelPath, expectedPath))
            {
                differences.Add(
                    $"模型路径[期望={expectedPath}，实际={actual.ModelPath}]");
            }

            AppendModelParameterDifferences(differences, actual.Model, target.ModelPlan);
            if (!actual.Model.HasSameParameters(target.ModelPlan)
                && !differences.Any(item => item.StartsWith("模型参数", StringComparison.Ordinal)))
            {
                differences.Add("模型参数存在未分类差异");
            }

            return differences.AsReadOnly();
        }

        private static void AppendModelParameterDifferences(
            ICollection<string> differences,
            FaceToFaceFrameModelSnapshot actual,
            FaceToFaceFrameModelPlan target)
        {
            var fields = new List<string>();
            AppendNumberDifference(fields, "宽", target.Width, actual.Width);
            AppendNumberDifference(fields, "长", target.Length, actual.Length);
            AppendNumberDifference(fields, "高", target.Height, actual.Height);
            AppendNumberDifference(fields, "进墙距离", target.WallEntryDistance, actual.WallEntryDistance);
            if (target.FrameTemplateKind == FaceToWallFrameTemplateKind.Large1 && !actual.HasWallEntryDistanceProperty)
                fields.Add("进墙距离自定义属性缺失");
            if (actual.SideCoverPlateWidth.HasValue
                && target.SideCoverPlateWidth.HasValue)
            {
                AppendNumberDifference(
                    fields,
                    "侧盖板宽度",
                    target.SideCoverPlateWidth.Value,
                    actual.SideCoverPlateWidth.Value);
            }
            if (actual.FrameTemplateKind != target.FrameTemplateKind)
            {
                fields.Add(
                    $"模板类型[期望={target.FrameTemplateKind}，实际={actual.FrameTemplateKind}]");
            }
            if (actual.TemplateIdentity != target.TemplateIdentity)
            {
                fields.Add(
                    $"模板身份[期望={target.TemplateIdentity}，实际={actual.TemplateIdentity}]");
            }

            AppendSmallOffsetDifferences(fields, actual.SmallOffsetFrame, target.SmallOffsetFrame);
            if (actual.InsulatorCount != target.InsulatorCount)
            {
                fields.Add(
                    $"绝缘子数量[期望={target.InsulatorCount}，实际={actual.InsulatorCount}]");
            }
            if (!FaceToFaceFramePhysicalIdentity.SequenceEqual(
                    actual.InsulatorPositions,
                    target.InsulatorPositions))
            {
                fields.Add(
                    $"绝缘子定位[期望={FormatNumbers(target.InsulatorPositions)}，"
                    + $"实际={FormatNumbers(actual.InsulatorPositions)}]");
            }

            if (fields.Count > 0)
            {
                differences.Add("模型参数{" + string.Join("，", fields) + "}");
            }
        }

        private static void AppendSmallOffsetDifferences(
            ICollection<string> fields,
            FaceToFaceSmallOffsetFrameDrivePlan actual,
            FaceToFaceSmallOffsetFrameDrivePlan target)
        {
            if (actual == null || target == null)
            {
                if (actual != null || target != null)
                {
                    fields.Add(
                        $"小偏移结构[期望={(target == null ? "无" : "有")}，"
                        + $"实际={(actual == null ? "无" : "有")}]");
                }
                return;
            }

            AppendNumberDifference(fields, "小偏移宽", target.FrameWidth, actual.FrameWidth);
            if (actual.InletOffsetDirection != target.InletOffsetDirection)
            {
                fields.Add(
                    $"进孔偏移方向[期望={target.InletOffsetDirection}，实际={actual.InletOffsetDirection}]");
            }
            AppendNumberDifference(
                fields,
                "进孔偏移尺寸",
                target.InletOffsetSize,
                actual.InletOffsetSize);
            if (actual.OutletOffsetDirection != target.OutletOffsetDirection)
            {
                fields.Add(
                    $"出孔偏移方向[期望={target.OutletOffsetDirection}，实际={actual.OutletOffsetDirection}]");
            }
            AppendNumberDifference(
                fields,
                "出孔偏移尺寸",
                target.OutletOffsetSize,
                actual.OutletOffsetSize);
        }

        private static void AppendNumberDifference(
            ICollection<string> fields,
            string fieldName,
            double expected,
            double actual)
        {
            if (!FaceToFaceFramePhysicalIdentity.AreEqual(expected, actual))
            {
                fields.Add($"{fieldName}[期望={expected:0.###}，实际={actual:0.###}]");
            }
        }

        private static string FormatRoute(string inlet, string outlet)
        {
            return (string.IsNullOrWhiteSpace(inlet) ? "<空>" : inlet)
                + "/"
                + (string.IsNullOrWhiteSpace(outlet) ? "<空>" : outlet);
        }

        private static string FormatNumbers(IEnumerable<double> values)
        {
            return "[" + string.Join(",", (values ?? Enumerable.Empty<double>())
                .Select(value => value.ToString("0.###"))) + "]";
        }

        private static bool ActualRouteMatchesTarget(
            FaceToFaceFramePositionSnapshot actual,
            FaceToFaceFramePositionPlan target)
        {
            if (!string.IsNullOrWhiteSpace(actual.InletCoordinateName))
            {
                // 链尾没有后继配合，可能反读不到出口；入口已能唯一确定所用坐标系组。
                return string.Equals(
                        actual.InletCoordinateName,
                        target.InletCoordinateName,
                        StringComparison.Ordinal)
                    && (string.IsNullOrWhiteSpace(actual.OutletCoordinateName)
                        || string.Equals(
                            actual.OutletCoordinateName,
                            target.OutletCoordinateName,
                            StringComparison.Ordinal));
            }

            if (actual.Model.FrameTemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner ||
                target.ModelPlan.FrameTemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner) return false;
            return actual.IsReversed == target.IsReversed;
        }

        private static bool PathsEqual(string first, string second)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(first ?? string.Empty),
                    Path.GetFullPath(second ?? string.Empty),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
