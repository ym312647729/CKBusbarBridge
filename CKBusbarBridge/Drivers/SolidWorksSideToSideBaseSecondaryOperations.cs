using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 侧对侧基座二次驱动的 SolidWorks、映射和文件事务实现。
    /// </summary>
    internal sealed class SolidWorksSideToSideBaseSecondaryOperations
        : ISideToSideBaseSecondaryOperations
    {
        private const string HostedInsulatorName = "绝缘子+垫片+支架";

        private readonly ISldWorks _swApp;
        private readonly string _projectPath;
        private readonly ISideToSideBaseTemplateLocator _templateLocator;
        private readonly ISideToSideBaseDriver _baseDriver;
        private readonly ISideToSideBaseInsulatorDriver _insulatorDriver;
        private readonly ISideToSideBaseInsulatorOperations _insulatorOperations;
        private readonly ISideToSideBaseModelOperations _modelOperations;
        private readonly IFaceToFaceBaseDirectoryCleaner _directoryCleaner;
        private IModelDoc2 _totalAssemblyModel;
        private AssemblyDoc _totalAssembly;

        internal SolidWorksSideToSideBaseSecondaryOperations(
            ISldWorks swApp,
            IModelDoc2 totalAssemblyModel,
            string projectPath,
            ISideToSideBaseTemplateLocator templateLocator,
            ISideToSideBaseDriver baseDriver,
            ISideToSideBaseInsulatorDriver insulatorDriver,
            ISideToSideBaseInsulatorOperations insulatorOperations,
            ISideToSideBaseModelOperations modelOperations,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _projectPath = string.IsNullOrWhiteSpace(projectPath)
                ? throw new ArgumentException("项目目录不能为空。", nameof(projectPath))
                : Path.GetFullPath(projectPath);
            _templateLocator = templateLocator
                ?? throw new ArgumentNullException(nameof(templateLocator));
            _baseDriver = baseDriver ?? throw new ArgumentNullException(nameof(baseDriver));
            _insulatorDriver = insulatorDriver
                ?? throw new ArgumentNullException(nameof(insulatorDriver));
            _insulatorOperations = insulatorOperations
                ?? throw new ArgumentNullException(nameof(insulatorOperations));
            _modelOperations = modelOperations
                ?? throw new ArgumentNullException(nameof(modelOperations));
            _directoryCleaner = directoryCleaner
                ?? throw new ArgumentNullException(nameof(directoryCleaner));
            SetTotalAssembly(totalAssemblyModel);
        }

        internal IBaseModelLifecycleTransaction CreateTransaction()
        {
            return new SideToSideLifecycleTransaction(this);
        }

        public SideToSideBaseSecondarySnapshot CaptureCurrentSnapshot(
            SideToSideCabinetParameterSnapshot parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            EnsureTotalAssemblyReady();
            try { _totalAssembly.ResolveAllLightWeightComponents(false); } catch { }
            var driver = new SolidWorksDriver(_swApp);
            var mappingJson = driver.GetCustomProperty(
                _totalAssemblyModel,
                SideToSideBaseModelMappingHelper.PropertyName);
            var positions = SideToSideBaseDrivePlanner.CreatePlans(
                parameters.ToParameters());
            ValidateMappingStructure(mappingJson, positions);
            var models = new List<SideToSideBasePhysicalModelSnapshot>();
            foreach (var position in positions)
            {
                models.Add(CaptureModel(mappingJson, position, position.BaseModel));
                if (position.ElevatedBaseModel != null)
                {
                    models.Add(CaptureModel(
                        mappingJson,
                        position,
                        position.ElevatedBaseModel));
                }
            }

            return new SideToSideBaseSecondarySnapshot(parameters, models);
        }

        public void ApplyPlan(
            string projectPath,
            SideToSideBaseSecondaryDrivePlan plan,
            IReadOnlyList<SideToSideBasePositionPlan> targetPositions,
            IDrawingNumberGenerator drawingNumbers,
            IBaseModelLifecycleTransaction transaction)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (targetPositions == null) throw new ArgumentNullException(nameof(targetPositions));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (!string.Equals(
                Path.GetFullPath(projectPath),
                _projectPath,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("基座二次驱动项目目录与操作上下文不一致。");
            }

            var templatePaths = ResolvePendingTemplateAssemblyPaths(plan);
            var positionApplications = SideToSideBasePositionApplicationPlanner.Create(
                plan.PositionActions,
                templatePaths.Values);
            var allowedDetachedModelPaths = SideToSideBaseModelOwnershipPolicy
                .ResolveAllowedDetachedModelPaths(
                    _projectPath,
                    positionApplications,
                    templatePaths.Values);

            BackupAffectedModels(plan, transaction);
            var firstFrameAnchors = new SideToSideBaseFirstFrameAnchors(
                DetachFirstFrameAnchor, RestoreFirstFrameAnchor);
            var closeDriver = new SolidWorksDriver(_swApp);
            var preparedPaths = SideToSideBaseTemplateConflictLifecycle
                .PrepareAfterDetachingConflicts(
                    positionApplications,
                    models => RemoveOldPosition(models, firstFrameAnchors),
                    () => new SideToSideBaseAssembler(
                        _swApp,
                        _totalAssemblyModel).SaveAndRebuild(),
                    path =>
                    {
                        var safePath = BaseModelTransactionPathGuard
                            .EnsureSafeProjectModelOrTemplate(
                                _projectPath,
                                path,
                                allowedDetachedModelPaths);
                        closeDriver.CloseDocumentByPathConfirmed(safePath);
                        EnsureTotalAssemblyReady();
                    },
                    () => DriveReservedExistingModels(
                        plan,
                        targetPositions,
                        transaction),
                    () => PrepareTemplateModels(
                        plan,
                        targetPositions,
                        templatePaths,
                        drawingNumbers,
                        transaction));
            AddExistingModelPaths(plan, preparedPaths);

            var tokens = ApplyPositionActions(positionApplications, preparedPaths, firstFrameAnchors);

            var assembler = new SideToSideBaseAssembler(_swApp, _totalAssemblyModel);
            assembler.WriteModelMapping(
                SideToSideBaseModelMappingHelper.BuildJson(tokens));
            new SolidWorksParentDimensionDriver(_swApp, path => BridgeGenerationFileBackup.Under(_projectPath, path))
                .VerifySharedBaseDimensions(_totalAssemblyModel, tokens.Where(token =>
                    SolidWorksBaseBridgeAngleDriver.IsOrdinaryBasePath(token.ModelPath)).Select(token => FindComponent(token.ComponentFullName)));
            RetireObsoleteModels(plan, transaction);
        }

        private void BackupAffectedModels(
            SideToSideBaseSecondaryDrivePlan plan,
            IBaseModelLifecycleTransaction transaction)
        {
            var paths = plan.PositionActions
                .SelectMany(action => action.OldModels)
                .Where(model => SideToSideBaseModelOwnershipPolicy.IsProjectOwned(
                    _projectPath,
                    model.ModelPath))
                .Select(model => model.ModelPath)
                .Concat(plan.ModelReusePlan.ModelActions
                    .Where(action => action.SourceKind == BaseModelSourceKind.Existing
                        && action.NeedsDrive
                        && SideToSideBaseModelOwnershipPolicy.IsProjectOwned(
                            _projectPath,
                            action.ExistingModel.ModelPath))
                    .Select(action => action.ExistingModel.ModelPath))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                transaction.BackupModelDirectory(path);
                foreach (var directory in SolidWorksParentDimensionDriver.ProjectReferenceDirectories(_swApp, path, _projectPath))
                    transaction.BackupModelDirectory(directory);
            }
        }

        private IReadOnlyDictionary<SideToSideBaseModelKind, string>
            ResolvePendingTemplateAssemblyPaths(
                SideToSideBaseSecondaryDrivePlan plan)
        {
            var paths = new Dictionary<SideToSideBaseModelKind, string>();
            foreach (var action in plan.ModelReusePlan.ModelActions
                .Where(item => item.SourceKind == BaseModelSourceKind.Template))
            {
                var kind = RequireModelPlan(action).Kind;
                if (!paths.ContainsKey(kind))
                {
                    paths.Add(kind, _templateLocator.GetTemplateAssemblyPath(kind));
                }
            }

            return paths;
        }

        private IDictionary<string, string> PrepareTemplateModels(
            SideToSideBaseSecondaryDrivePlan plan,
            IReadOnlyList<SideToSideBasePositionPlan> targetPositions,
            IReadOnlyDictionary<SideToSideBaseModelKind, string> templatePaths,
            IDrawingNumberGenerator drawingNumbers,
            IBaseModelLifecycleTransaction transaction)
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var action in plan.ModelReusePlan.ModelActions
                .Where(item => item.SourceKind == BaseModelSourceKind.Template))
            {
                var modelPlan = RequireModelPlan(action);
                var position = targetPositions.Single(item => string.Equals(
                    item.PositionName,
                    action.RepresentativeTarget.PositionName,
                    StringComparison.Ordinal));
                var outputDirectory = Path.Combine(
                    _projectPath,
                    "基座",
                    position.PositionName,
                    GetModelFolderName(modelPlan.Kind));
                if (Directory.Exists(outputDirectory))
                {
                    throw new IOException(
                        "待新增侧对侧基座目录已存在，拒绝覆盖：" + outputDirectory);
                }

                transaction.RegisterNewDirectory(outputDirectory);
                var placements = GetHostedPlacements(position, modelPlan.Kind);
                var insulatorPaths = new List<string>();
                if (placements.Count > 0)
                {
                    foreach (var placement in placements)
                    {
                        insulatorPaths.Add(PrepareInsulator(
                            placement,
                            transaction));
                    }
                }

                var modelPath = _baseDriver.PrepareModel(
                    modelPlan,
                    templatePaths[modelPlan.Kind],
                    outputDirectory,
                    drawingNumbers);
                if (placements.Count > 0)
                {
                    _baseDriver.InstallInsulators(
                        modelPath,
                        insulatorPaths,
                        placements);
                }

                paths.Add(action.TargetKey, modelPath);
            }

            return paths;
        }

        private static void AddExistingModelPaths(
            SideToSideBaseSecondaryDrivePlan plan,
            IDictionary<string, string> paths)
        {
            foreach (var action in plan.ModelReusePlan.ModelActions
                .Where(item => item.SourceKind == BaseModelSourceKind.Existing))
            {
                paths.Add(action.TargetKey, action.ExistingModel.ModelPath);
            }
        }

        private IReadOnlyList<SideToSideBaseAssemblyToken> ApplyPositionActions(
            IReadOnlyList<SideToSideBasePositionApplication> applications,
            IDictionary<string, string> preparedPaths,
            SideToSideBaseFirstFrameAnchors firstFrameAnchors)
        {
            var tokens = new List<SideToSideBaseAssemblyToken>();
            var assembler = new SideToSideBaseAssembler(_swApp, _totalAssemblyModel);
            foreach (var application in applications)
            {
                var action = application.Action;
                if (application.RemoveDuringPositionApplication)
                {
                    RemoveOldPosition(action.OldModels, firstFrameAnchors);
                }

                if (application.PreserveOldMapping)
                {
                    tokens.AddRange(action.OldModels.Select(ToMappingToken));
                    continue;
                }
                if (!application.InsertDuringPositionApplication)
                {
                    continue;
                }

                tokens.AddRange(InsertTargetPosition(
                    assembler,
                    action,
                    preparedPaths));
            }

            // 新普通基座实例齐备后恢复原首基架连接；恢复或端点校验失败会进入外层事务回滚。
            firstFrameAnchors.RestoreBeforeSave(tokens);
            assembler.SaveAndRebuild();
            return tokens;
        }

        private IReadOnlyList<SideToSideBaseAssemblyToken> InsertTargetPosition(
            SideToSideBaseAssembler assembler,
            SideToSideBasePositionAction action,
            IDictionary<string, string> preparedPaths)
        {
            var position = action.TargetPlan
                ?? throw new InvalidOperationException(
                    action.PositionName + "缺少目标基座计划。");
            if (position.BaseModel.Kind == SideToSideBaseModelKind.Base1)
            {
                return new[]
                {
                    assembler.InsertBase1AtRoot(
                        position,
                        preparedPaths[action.TargetModelKeys[SideToSideBaseModelKind.Base1]])
                };
            }

            var elevated = assembler.InsertElevatedAtRoot(
                position,
                preparedPaths[action.TargetModelKeys[SideToSideBaseModelKind.ElevatedBase]]);
            var base2 = assembler.InsertBase2OnElevated(
                position,
                preparedPaths[action.TargetModelKeys[SideToSideBaseModelKind.Base2]],
                elevated);
            return new[] { elevated, base2 };
        }

        private void DriveReservedExistingModels(
            SideToSideBaseSecondaryDrivePlan plan,
            IReadOnlyList<SideToSideBasePositionPlan> targetPositions,
            IBaseModelLifecycleTransaction transaction)
        {
            foreach (var action in plan.ModelReusePlan.ModelActions
                .Where(item => item.SourceKind == BaseModelSourceKind.Existing
                    && item.NeedsDrive))
            {
                var modelPlan = RequireModelPlan(action);
                var modelPath = action.ExistingModel.ModelPath;
                if (!SideToSideBaseModelOwnershipPolicy.IsProjectOwned(
                    _projectPath,
                    modelPath))
                {
                    throw new InvalidOperationException(
                        "拒绝原地驱动项目目录外的侧对侧基座：" + modelPath);
                }
                _modelOperations.ApplyRequiredVariablesAndSave(
                    modelPath,
                    modelPlan.GlobalVariables,
                    ResolveElevatedState(modelPlan.Kind),
                    keepModelOpenAfterSave: false);

                var position = targetPositions.Single(item => string.Equals(
                    item.PositionName,
                    action.RepresentativeTarget.PositionName,
                    StringComparison.Ordinal));
                var placements = GetHostedPlacements(position, modelPlan.Kind);
                if (placements.Count == 0)
                {
                    continue;
                }

                var physical = action.ExistingModel.Context
                    as SideToSideBasePhysicalModelSnapshot;
                var expectedDirectories = placements
                    .Select(placement => _insulatorDriver.ResolveOutputDirectory(
                        _projectPath,
                        placement,
                        placement.BracketHostSpan))
                    .ToArray();
                var canKeepInsulator = physical != null
                    && physical.InsulatorPaths.Count == expectedDirectories.Length
                    && expectedDirectories.All(directory =>
                        physical.InsulatorPaths.Any(path => IsUnderDirectory(
                            path,
                            directory)));
                if (canKeepInsulator)
                {
                    WriteHostPlacements(modelPath, placements);
                    continue;
                }

                var insulatorPaths = placements
                    .Select(placement => PrepareInsulator(placement, transaction))
                    .ToArray();
                ReplaceHostedInsulators(modelPath, insulatorPaths, placements);
            }
        }

        private string PrepareInsulator(
            SideToSideInsulatorPlacementPlan placement,
            IBaseModelLifecycleTransaction transaction)
        {
            var directory = _insulatorDriver.ResolveOutputDirectory(
                _projectPath,
                placement,
                placement.BracketHostSpan);
            if (Directory.Exists(directory))
            {
                transaction.BackupModelDirectory(directory);
            }
            else
            {
                transaction.RegisterNewDirectory(directory);
            }

            return _insulatorDriver.PrepareInsulator(
                _projectPath,
                placement,
                placement.BracketHostSpan);
        }

        private void WriteHostPlacements(
            string hostModelPath,
            IReadOnlyList<SideToSideInsulatorPlacementPlan> placements)
        {
            var session = _insulatorOperations.OpenHostAssembly(hostModelPath);
            var succeeded = false;
            try
            {
                foreach (var placement in placements)
                {
                    _insulatorOperations.WritePlacement(session, placement);
                }

                succeeded = true;
            }
            finally
            {
                _insulatorOperations.CompleteHost(
                    session,
                    hostModelPath,
                    succeeded,
                    keepHostOpen: false,
                    closeInsertedComponentDocuments: false);
            }
        }

        private void ReplaceHostedInsulators(
            string hostModelPath,
            IReadOnlyList<string> insulatorPaths,
            IReadOnlyList<SideToSideInsulatorPlacementPlan> placements)
        {
            var driver = new SolidWorksDriver(_swApp);
            IModelDoc2 hostModel = null;
            try
            {
                hostModel = driver.OpenOrActivateDocument(hostModelPath);
                var assembly = hostModel as AssemblyDoc;
                if (assembly == null)
                {
                    throw new InvalidOperationException(
                        "绝缘子宿主不是有效装配体：" + hostModelPath);
                }

                try { assembly.ResolveAllLightWeightComponents(false); } catch { }
                var hosted = GetFirstLevelHostedInsulators(hostModel);
                hostModel.ClearSelection2(true);
                var append = false;
                foreach (var component in hosted)
                {
                    if (!component.Select(append))
                    {
                        throw new InvalidOperationException(
                            "无法选择待替换的宿主绝缘子：" + component.Name2);
                    }
                    append = true;
                }

                if (hosted.Count > 0
                    && !assembly.DeleteSelections(
                        (int)swDeleteSelectionOptions_e.swDelete_Children))
                {
                    throw new InvalidOperationException(
                        "SolidWorks 未确认删除旧宿主绝缘子：" + hostModelPath);
                }

                SaveModel(hostModel, "保存已移除旧绝缘子的宿主失败");
            }
            finally
            {
                if (hostModel != null)
                {
                    driver.TryCloseDocumentByPath(hostModelPath);
                    SolidWorksFileHelper.ReleaseComReference(hostModel);
                }
                SWHelper.EnsureAssemblyEditingContext(_swApp, _totalAssemblyModel);
            }

            _baseDriver.InstallInsulators(hostModelPath, insulatorPaths, placements);
        }

        private void RetireObsoleteModels(
            SideToSideBaseSecondaryDrivePlan plan,
            IBaseModelLifecycleTransaction transaction)
        {
            foreach (var retirement in plan.ModelReusePlan.Retirements)
            {
                if (!SideToSideBaseModelOwnershipPolicy.IsProjectOwned(
                    _projectPath,
                    retirement.ModelPath))
                {
                    continue;
                }

                var directory = BaseModelTransactionPathGuard
                    .EnsureSafeProjectModelDescendant(
                        _projectPath,
                        Path.GetDirectoryName(retirement.ModelPath));
                transaction.BackupModelDirectory(directory);
                new SolidWorksDriver(_swApp).CloseOpenDocumentsUnderDirectoryConfirmed(
                    directory,
                    new[] { _totalAssemblyModel.GetPathName() });
                if (Directory.Exists(directory)
                    && !_directoryCleaner.TryDelete(directory, out var error))
                {
                    throw new IOException(
                        "退役侧对侧基座目录失败：" + directory,
                        error);
                }
            }
        }

        private SideToSideBasePhysicalModelSnapshot CaptureModel(
            string mappingJson,
            SideToSideBasePositionPlan position,
            SideToSideBaseModelPlan modelPlan)
        {
            if (!SideToSideBaseModelMappingHelper.TryGetComponentFullName(
                mappingJson,
                position.PositionName,
                modelPlan.Kind,
                out var componentFullName))
            {
                throw new InvalidOperationException(
                    "模型对照映射缺少"
                    + position.PositionName
                    + "/"
                    + modelPlan.Kind
                    + "组件。");
            }

            var component = FindComponent(componentFullName);
            var modelPath = component?.GetPathName();
            var model = component?.GetModelDoc2() as IModelDoc2;
            if (component == null
                || string.IsNullOrWhiteSpace(modelPath)
                || !File.Exists(modelPath)
                || model == null)
            {
                throw new FileNotFoundException(
                    "模型对照映射指向的侧对侧基座不可读：" + componentFullName,
                    modelPath);
            }

            EnsureGlobalVariables(model, modelPlan.GlobalVariables, modelPath);
            var placements = GetHostedPlacements(position, modelPlan.Kind);
            var hosted = GetFirstLevelHostedInsulators(model);
            if (hosted.Count != placements.Count)
            {
                throw new InvalidOperationException(
                    position.PositionName
                    + "/"
                    + modelPlan.Kind
                    + "宿主绝缘子实例数不一致；期望="
                    + placements.Count
                    + "；实际="
                    + hosted.Count);
            }
            foreach (var placement in placements)
            {
                EnsurePlacement(model, placement, modelPath);
            }

            var templatePath = _templateLocator.GetTemplateAssemblyPath(modelPlan.Kind);
            var templateName = Path.GetFileNameWithoutExtension(templatePath);
            var currentName = Path.GetFileNameWithoutExtension(modelPath);
            var suffix = BaseModelFileNamePolicy.ResolveCurrentSuffix(
                templateName,
                currentName);
            var directory = Path.GetDirectoryName(Path.GetFullPath(modelPath));
            var obsoleteFiles = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                : new[] { modelPath };
            return new SideToSideBasePhysicalModelSnapshot(
                position.PositionName,
                modelPlan.Kind,
                componentFullName,
                modelPath,
                hosted.Select(componentItem => componentItem.GetPathName()),
                SideToSideBaseLifecycleAdapter.CreateIdentity(position, modelPlan),
                new BaseModelNameState(templateName, currentName, suffix),
                obsoleteFiles);
        }

        private void ValidateMappingStructure(
            string mappingJson,
            IReadOnlyList<SideToSideBasePositionPlan> positions)
        {
            if (string.IsNullOrWhiteSpace(mappingJson))
            {
                if (positions.Count == 0)
                {
                    return;
                }

                throw new InvalidOperationException(
                    "总装配体缺少“模型对照映射”，无法捕获侧对侧基座快照。");
            }

            var enabled = positions.ToDictionary(
                position => position.PositionName,
                StringComparer.Ordinal);
            foreach (var positionName in SideToSideBaseLifecycleAdapter.PositionOrder)
            {
                var hasOrdinary = SideToSideBaseModelMappingHelper.TryGetComponentFullName(
                    mappingJson,
                    positionName,
                    SideToSideBaseModelKind.Base1,
                    out _);
                var hasElevated = SideToSideBaseModelMappingHelper.TryGetComponentFullName(
                    mappingJson,
                    positionName,
                    SideToSideBaseModelKind.ElevatedBase,
                    out _);
                if (!enabled.TryGetValue(positionName, out var plan))
                {
                    if (hasOrdinary || hasElevated)
                    {
                        throw new InvalidOperationException(
                            "已停用位置仍在模型对照映射中引用基座：" + positionName);
                    }
                    continue;
                }

                if (!hasOrdinary || hasElevated != plan.HasElevatedBase)
                {
                    throw new InvalidOperationException(
                        positionName + "的普通/增高基座实际结构与参数快照不一致。");
                }
            }
        }

        private void RemoveOldPosition(
            IReadOnlyList<SideToSideBasePhysicalModelSnapshot> oldModels,
            SideToSideBaseFirstFrameAnchors firstFrameAnchors)
        {
            foreach (var model in oldModels
                .OrderBy(item => RemovalPriority(item.Kind)))
            {
                firstFrameAnchors.CaptureBeforeRemove(model.PositionName, model.Kind, model.ComponentFullName);
                var component = FindComponent(model.ComponentFullName)
                    ?? throw new InvalidOperationException(
                        "无法找到待移除基座组件：" + model.ComponentFullName);
                _totalAssemblyModel.ClearSelection2(true);
                try
                {
                    if (!component.Select(false)
                        || !_totalAssembly.DeleteSelections(
                            (int)swDeleteSelectionOptions_e.swDelete_Children))
                    {
                        throw new InvalidOperationException(
                            "SolidWorks 未确认移除基座组件：" + model.ComponentFullName);
                    }
                }
                finally
                {
                    _totalAssemblyModel.ClearSelection2(true);
                }
            }
        }

        private FaceToFaceBaseFirstFrameAnchor DetachFirstFrameAnchor(string positionName, string componentFullName)
        {
            EnsureTotalAssemblyReady();
            var matches = ReadFirstFrameAnchorMatches(positionName, componentFullName);
            if (matches.Count == 0) return null;
            if (matches.Count != 1)
                throw new InvalidOperationException("普通基座存在多条首基架锚点配合，无法安全替换：" + componentFullName);
            var match = matches[0];
            if (string.IsNullOrWhiteSpace(match.Item1))
                throw new InvalidOperationException("无法取得普通基座与首基架的配合特征名称：" + componentFullName);
            SWHelper.TryDeleteAssemblyMatesByName(_totalAssemblyModel, new[] { match.Item1 });
            if (ReadFirstFrameAnchorMatches(positionName, componentFullName).Count != 0)
                throw new InvalidOperationException("解除普通基座与首基架配合失败：" + componentFullName);
            return match.Item2;
        }

        private IReadOnlyList<Tuple<string, FaceToFaceBaseFirstFrameAnchor>> ReadFirstFrameAnchorMatches(
            string positionName, string componentFullName)
        {
            var component = FindComponent(componentFullName)
                ?? throw new InvalidOperationException("未找到首基架连接的普通基座：" + componentFullName);
            var matches = new List<Tuple<string, FaceToFaceBaseFirstFrameAnchor>>();
            foreach (var item in (component.GetMates() as object[]) ?? Array.Empty<object>())
            {
                if (!(item is IMate2 mate)) continue;
                var anchor = SolidWorksFaceToFaceBaseSecondaryOperations.ResolveFirstFrameAnchor(
                    positionName, component.Name2, ReadFrameMateEndpoints(mate));
                if (anchor != null)
                    matches.Add(Tuple.Create((item as Feature)?.Name ?? (item as IFeature)?.Name, anchor));
            }
            return matches;
        }

        private void RestoreFirstFrameAnchor(SideToSideBaseAssemblyToken ordinaryBase, FaceToFaceBaseFirstFrameAnchor anchor)
        {
            if (ordinaryBase.Kind == SideToSideBaseModelKind.ElevatedBase
                || !string.Equals(ordinaryBase.PositionName, anchor.PositionName, StringComparison.Ordinal))
                throw new InvalidOperationException("恢复首基架连接时，新普通基座类型或位置与记录不一致。");
            EnsureTotalAssemblyReady();
            _totalAssembly.ResolveAllLightWeightComponents(false);
            var baseComponent = FindComponent(ordinaryBase.ComponentFullName)
                ?? throw new InvalidOperationException("未找到回装后的普通基座：" + ordinaryBase.ComponentFullName);
            var frameComponent = FindComponent(anchor.FrameComponentFullName)
                ?? throw new InvalidOperationException("未找到原首基架：" + anchor.FrameComponentFullName);
            if (!PublicMethod.AddMateUseCoord(_swApp, _totalAssembly, baseComponent, frameComponent,
                FaceToFaceFrameChainAnalyzer.BaseInstallationCoordinateName, anchor.FrameCoordinateName))
                throw new InvalidOperationException("恢复普通基座与首基架配合失败：" + anchor.PositionName);
            FaceToFaceBaseOperationGuard.EnsureRebuildSucceeded(
                _totalAssemblyModel.EditRebuild3(), anchor.PositionName + "首基架锚点恢复", _totalAssemblyModel.GetPathName());
            var endpointSets = ((baseComponent.GetMates() as object[]) ?? Array.Empty<object>())
                .OfType<IMate2>().Select(ReadFrameMateEndpoints).ToArray();
            if (!SolidWorksFaceToFaceBaseSecondaryOperations.PositioningMateSetIsExact(
                endpointSets, frameComponent.Name2, anchor.FrameCoordinateName,
                baseComponent.Name2, FaceToFaceFrameChainAnalyzer.BaseInstallationCoordinateName))
                throw new InvalidOperationException("恢复普通基座与首基架配合后，端点校验失败：" + anchor.PositionName);
        }

        private IReadOnlyCollection<Tuple<string, string>> ReadFrameMateEndpoints(IMate2 mate)
        {
            var endpoints = new List<Tuple<string, string>>();
            for (var index = 0; index < mate.GetMateEntityCount(); index++)
            {
                var entity = mate.MateEntity(index) as MateEntity2;
                if (entity == null) continue;
                var reference = entity.ReferenceComponent;
                endpoints.Add(Tuple.Create(
                    SolidWorksFaceToFaceFrameReadOperations.NormalizeMateComponentInstanceId(
                        reference?.Name2, reference?.GetPathName(), _totalAssemblyModel.GetPathName()),
                    (entity.Reference as IFeature)?.Name));
            }
            return endpoints;
        }

        private Component2 FindComponent(string componentFullName)
        {
            var root = _totalAssemblyModel.ConfigurationManager?
                .ActiveConfiguration?
                .GetRootComponent3(true) as Component2;
            return FindComponent(root, componentFullName);
        }

        private static Component2 FindComponent(
            Component2 parent,
            string componentFullName)
        {
            if (parent == null)
            {
                return null;
            }
            var children = parent.GetChildren() as object[];
            if (children == null)
            {
                return null;
            }

            foreach (var child in children.OfType<Component2>())
            {
                if (string.Equals(
                    child.Name2,
                    componentFullName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }

                var nested = FindComponent(child, componentFullName);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static IReadOnlyList<Component2> GetFirstLevelHostedInsulators(
            IModelDoc2 hostModel)
        {
            var root = hostModel?.ConfigurationManager?
                .ActiveConfiguration?
                .GetRootComponent3(true) as Component2;
            var children = root?.GetChildren() as object[];
            return (children ?? new object[0])
                .OfType<Component2>()
                .Where(component => NormalizeComponentName(component.Name2)
                    .IndexOf(HostedInsulatorName, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();
        }

        private static string NormalizeComponentName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            var local = value;
            var slash = local.LastIndexOf('/');
            if (slash >= 0) local = local.Substring(slash + 1);
            var at = local.IndexOf('@');
            if (at >= 0) local = local.Substring(0, at);
            return local;
        }

        private static void EnsureGlobalVariables(
            IModelDoc2 model,
            IReadOnlyDictionary<string, double> expected,
            string modelPath)
        {
            foreach (var pair in expected)
            {
                if (!CabinetAssemblyDimensionReader.TryGetGlobalVariableValue(
                    model,
                    pair.Key,
                    out var value)
                    || !SideToSideCabinetChangeAnalyzer.SameNumber(value, pair.Value))
                {
                    throw new InvalidOperationException(
                        "侧对侧基座全局变量与快照不一致："
                        + modelPath
                        + "；"
                        + CabinetHoleParameters.GetParameterDisplayName(pair.Key));
                }
            }
        }

        private static void EnsurePlacement(
            IModelDoc2 hostModel,
            SideToSideInsulatorPlacementPlan placement,
            string modelPath)
        {
            var dimension = hostModel.Parameter(
                placement.PlacementDimensionName) as Dimension;
            if (dimension == null
                || !SideToSideCabinetChangeAnalyzer.SameNumber(
                    dimension.SystemValue * 1000d,
                    placement.PlacementHeight))
            {
                throw new InvalidOperationException(
                    "侧对侧绝缘子定位与快照不一致："
                    + modelPath
                    + "；"
                    + placement.PlacementDimensionName);
            }
        }

        private static SideToSideBaseAssemblyToken ToMappingToken(
            SideToSideBasePhysicalModelSnapshot model)
        {
            return new SideToSideBaseAssemblyToken(
                model.PositionName,
                model.Kind,
                model.ModelPath,
                null,
                model.ComponentFullName);
        }

        private static SideToSideBaseModelPlan RequireModelPlan(
            BaseModelReuseAction action)
        {
            return action.RepresentativeTarget.Context as SideToSideBaseModelPlan
                ?? throw new InvalidOperationException(
                    "侧对侧基座复用动作缺少模型计划：" + action.TargetKey);
        }

        private static IReadOnlyList<SideToSideInsulatorPlacementPlan> GetHostedPlacements(
            SideToSideBasePositionPlan position,
            SideToSideBaseModelKind kind)
        {
            return position.Insulators
                .Where(placement => placement.HostModelKind == kind)
                .ToArray();
        }

        private static bool? ResolveElevatedState(SideToSideBaseModelKind kind)
        {
            return kind == SideToSideBaseModelKind.Base1
                ? false
                : kind == SideToSideBaseModelKind.Base2
                    ? true
                    : (bool?)null;
        }

        private static string GetModelFolderName(SideToSideBaseModelKind kind)
        {
            switch (kind)
            {
                case SideToSideBaseModelKind.Base1: return "基座1";
                case SideToSideBaseModelKind.Base2: return "基座2";
                case SideToSideBaseModelKind.ElevatedBase: return "增高基座";
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static int RemovalPriority(SideToSideBaseModelKind kind)
        {
            return kind == SideToSideBaseModelKind.Base2 ? 0 : 1;
        }

        private static bool IsUnderDirectory(string path, string directory)
        {
            var root = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(
                root,
                StringComparison.OrdinalIgnoreCase);
        }

        private static void SaveModel(IModelDoc2 model, string failurePrefix)
        {
            FaceToFaceBaseOperationGuard.EnsureRebuildSucceeded(
                model.EditRebuild3(),
                failurePrefix,
                model.GetPathName());
            var errors = 0;
            var warnings = 0;
            var saved = model.Save3(
                (int)(swSaveAsOptions_e.swSaveAsOptions_Silent
                    | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                ref errors,
                ref warnings);
            if (!saved || errors != 0)
            {
                throw new InvalidOperationException(
                    failurePrefix
                    + "；errors="
                    + errors
                    + "；warnings="
                    + warnings);
            }
        }

        private void EnsureTotalAssemblyReady()
        {
            if (_totalAssemblyModel == null || _totalAssembly == null)
            {
                throw new InvalidOperationException("侧对侧总装配体上下文不可用。");
            }
            SWHelper.EnsureAssemblyEditingContext(_swApp, _totalAssemblyModel);
        }

        private void SetTotalAssembly(IModelDoc2 model)
        {
            _totalAssemblyModel = model
                ?? throw new ArgumentNullException(nameof(model));
            _totalAssembly = model as AssemblyDoc;
            if (_totalAssembly == null
                || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException("侧对侧二次驱动需要有效总装配体。");
            }
        }

        private sealed class SideToSideLifecycleTransaction
            : IBaseModelLifecycleTransaction
        {
            private readonly SolidWorksSideToSideBaseSecondaryOperations _owner;
            private readonly string _baseRoot;
            private readonly string _transactionRoot;
            private readonly Dictionary<string, string> _backups =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _newDirectories =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _createdFiles =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private string _baselineAssemblyPath;

            internal SideToSideLifecycleTransaction(
                SolidWorksSideToSideBaseSecondaryOperations owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _baseRoot = Path.Combine(owner._projectPath, "基座");
                Directory.CreateDirectory(_baseRoot);
                _transactionRoot = Path.Combine(
                    _baseRoot,
                    ".侧对侧二次驱动事务-" + Guid.NewGuid().ToString("N"));
            }

            public void SaveBaseline()
            {
                _owner.EnsureTotalAssemblyReady();
                Directory.CreateDirectory(_transactionRoot);
                var baselineDirectory = Path.Combine(_transactionRoot, "总装基线");
                Directory.CreateDirectory(baselineDirectory);
                SaveModel(_owner._totalAssemblyModel, "保存侧对侧总装事务基线失败");
                var totalPath = Path.GetFullPath(
                    _owner._totalAssemblyModel.GetPathName());
                _baselineAssemblyPath = Path.Combine(
                    baselineDirectory,
                    Path.GetFileName(totalPath));
                File.Copy(totalPath, _baselineAssemblyPath, true);
            }

            public void BackupModelDirectory(string modelPath)
            {
                var fullPath = Path.GetFullPath(modelPath);
                var directory = Directory.Exists(fullPath)
                    ? fullPath
                    : Path.GetDirectoryName(fullPath);
                directory = BaseModelTransactionPathGuard
                    .EnsureSafeProjectModelDescendant(
                        _owner._projectPath,
                        directory);
                if (_backups.Keys.Any(existing => BridgeGenerationFileBackup.Same(existing, directory) ||
                    BridgeGenerationFileBackup.Under(existing, directory)))
                {
                    return;
                }
                if (!Directory.Exists(directory))
                {
                    throw new DirectoryNotFoundException(
                        "待备份侧对侧模型目录不存在：" + directory);
                }

                var backup = Path.Combine(
                    _transactionRoot,
                    "模型备份",
                    _backups.Count.ToString("D2", CultureInfo.InvariantCulture));
                CopyDirectory(directory, backup);
                _backups.Add(directory, backup);
            }

            public void RegisterNewDirectory(string directoryPath)
            {
                var path = BaseModelTransactionPathGuard
                    .EnsureSafeProjectModelDescendant(
                        _owner._projectPath,
                        directoryPath);
                if (Directory.Exists(path))
                {
                    throw new IOException("事务新增目录已经存在：" + path);
                }
                _newDirectories.Add(path);
            }

            public void RegisterCreatedFiles(IEnumerable<string> filePaths)
            {
                if (filePaths == null) throw new ArgumentNullException(nameof(filePaths));
                foreach (var path in filePaths)
                {
                    var safe = BaseModelTransactionPathGuard
                        .EnsureSafeProjectModelDescendant(
                            _owner._projectPath,
                            path);
                    _createdFiles.Add(safe);
                }
            }

            public void Restore()
            {
                var totalPath = Path.GetFullPath(
                    _owner._totalAssemblyModel.GetPathName());
                var driver = new SolidWorksDriver(_owner._swApp);
                driver.CloseDocumentByPathConfirmed(totalPath);
                CloseModelRoots(driver);

                foreach (var directory in _newDirectories
                    .OrderByDescending(path => path.Length))
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, true);
                    }
                }
                foreach (var file in _createdFiles)
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                // 较早备份的精确子目录最后恢复，避免随后发现的祖先目录覆盖其基线。
                foreach (var backup in _backups.OrderBy(pair => pair.Key.Length))
                {
                    if (Directory.Exists(backup.Key))
                    {
                        Directory.Delete(backup.Key, true);
                    }
                    CopyDirectory(backup.Value, backup.Key);
                }

                if (string.IsNullOrWhiteSpace(_baselineAssemblyPath)
                    || !File.Exists(_baselineAssemblyPath))
                {
                    throw new FileNotFoundException(
                        "侧对侧总装事务基线不存在。",
                        _baselineAssemblyPath);
                }
                File.Copy(_baselineAssemblyPath, totalPath, true);
                var restored = driver.OpenOrActivateDocument(totalPath) as IModelDoc2;
                _owner.SetTotalAssembly(restored);
                Complete();
            }

            public void Complete()
            {
                if (!Directory.Exists(_transactionRoot))
                {
                    return;
                }
                BaseModelTransactionPathGuard.EnsureSafeDescendant(
                    _baseRoot,
                    _transactionRoot);
                Directory.Delete(_transactionRoot, true);
            }

            private void CloseModelRoots(SolidWorksDriver driver)
            {
                foreach (var name in new[] { "基座", "绝缘子" })
                {
                    var directory = Path.Combine(_owner._projectPath, name);
                    if (Directory.Exists(directory))
                    {
                        driver.CloseOpenDocumentsUnderDirectoryConfirmed(
                            directory,
                            null);
                    }
                }
            }

            private static void CopyDirectory(string source, string target)
            {
                Directory.CreateDirectory(target);
                foreach (var file in Directory.GetFiles(source))
                {
                    File.Copy(
                        file,
                        Path.Combine(target, Path.GetFileName(file)),
                        true);
                }
                foreach (var directory in Directory.GetDirectories(source))
                {
                    CopyDirectory(
                        directory,
                        Path.Combine(target, Path.GetFileName(directory)));
                }
            }
        }
    }
}
