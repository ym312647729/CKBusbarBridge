using CKBusbarBridge.Models;
using CKBusbarBridge.Helpers;
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
    /// 面对面基座二次驱动的 SolidWorks 与文件事务边界。
    /// </summary>
    internal sealed class SolidWorksFaceToFaceBaseSecondaryOperations
        : IFaceToFaceBaseSecondaryOperations
    {
        private readonly ISldWorks _swApp;
        private readonly IFaceToFaceBaseTemplateCopier _templateCopier;
        private readonly string _projectPath;
        private readonly string _baseRoot;
        private readonly string _transactionRoot;
        private readonly Dictionary<string, string> _directoryBackups =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _newDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _createdFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private IModelDoc2 _totalAssemblyModel;
        private AssemblyDoc _totalAssembly;
        private string _totalAssemblyPath;
        private string _baselineAssemblyPath;
        private readonly Func<
            CabinetDriverParameterSnapshot,
            IReadOnlyList<FaceToFaceBasePositionPlan>> _planFactory;
        private readonly IReadOnlyList<string> _orderedPositions;
        private readonly InsulatorDriveRecoverySession _insulatorRecovery;

        internal SolidWorksFaceToFaceBaseSecondaryOperations(
            ISldWorks swApp,
            IModelDoc2 totalAssemblyModel,
            string projectPath,
            IFaceToFaceBaseTemplateCopier templateCopier,
            InsulatorDriveRecoverySession insulatorRecovery = null)
            : this(
                swApp,
                totalAssemblyModel,
                projectPath,
                templateCopier,
                FaceToFaceBaseDrivePlanner.CreatePlans,
                FaceToFaceBaseModelMappingHelper.FaceToFaceOrderedPositions,
                insulatorRecovery)
        {
        }

        internal SolidWorksFaceToFaceBaseSecondaryOperations(
            ISldWorks swApp,
            IModelDoc2 totalAssemblyModel,
            string projectPath,
            IFaceToFaceBaseTemplateCopier templateCopier,
            Func<CabinetDriverParameterSnapshot, IReadOnlyList<FaceToFaceBasePositionPlan>> planFactory,
            IReadOnlyList<string> orderedPositions,
            InsulatorDriveRecoverySession insulatorRecovery = null)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _templateCopier = templateCopier
                ?? throw new ArgumentNullException(nameof(templateCopier));
            _projectPath = string.IsNullOrWhiteSpace(projectPath)
                ? throw new ArgumentException("项目路径不能为空。", nameof(projectPath))
                : Path.GetFullPath(projectPath);
            _baseRoot = Path.Combine(_projectPath, "基座");
            _transactionRoot = Path.Combine(
                _baseRoot,
                ".__secondary_transaction_" + Guid.NewGuid().ToString("N"));
            _planFactory = planFactory ?? throw new ArgumentNullException(nameof(planFactory));
            _orderedPositions = orderedPositions
                ?? throw new ArgumentNullException(nameof(orderedPositions));
            _insulatorRecovery = insulatorRecovery;
            SetTotalAssembly(totalAssemblyModel);
        }

        public FaceToFaceBaseSecondarySnapshot CaptureCurrentSnapshot(
            CabinetDriverParameterSnapshot parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            EnsureTotalAssemblyReady();
            var mappingJson = new SolidWorksDriver(_swApp).GetCustomProperty(
                _totalAssemblyModel,
                FaceToFaceBaseModelMappingHelper.PropertyName);
            if (string.IsNullOrWhiteSpace(mappingJson))
            {
                throw new InvalidOperationException("总装配体缺少“模型对照映射”，无法捕获基座快照。");
            }

            var plans = _planFactory(parameters);
            try { _totalAssembly.ResolveAllLightWeightComponents(false); } catch { }
            ValidatePositionStructure(mappingJson, plans);
            var models = new List<FaceToFaceBasePhysicalModelSnapshot>();
            foreach (var position in plans.OrderBy(x => PositionPriority(x.PositionName)))
            {
                models.Add(CapturePhysicalModel(
                    mappingJson,
                    position,
                    FaceToFaceBaseModelKind.Base));
                if (position.ElevatedBaseModel != null)
                {
                    models.Add(CapturePhysicalModel(
                        mappingJson,
                        position,
                        FaceToFaceBaseModelKind.ElevatedBase));
                }
            }

            return new FaceToFaceBaseSecondarySnapshot(parameters, models);
        }

        public void SaveBaseline()
        {
            EnsureTotalAssemblyReady();
            Directory.CreateDirectory(_transactionRoot);
            var baselineDirectory = Path.Combine(_transactionRoot, "总装基线");
            Directory.CreateDirectory(baselineDirectory);
            SaveTotalAssembly(rebuild: false);
            _baselineAssemblyPath = Path.Combine(
                baselineDirectory,
                Path.GetFileName(_totalAssemblyPath));
            File.Copy(_totalAssemblyPath, _baselineAssemblyPath, overwrite: true);
        }

        public void BackupModelDirectory(string modelPath)
        {
            var fullPath = Path.GetFullPath(modelPath);
            BackupDirectory(Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath));
            foreach (var directory in SolidWorksParentDimensionDriver.ProjectReferenceDirectories(_swApp, fullPath, _projectPath))
                BackupDirectory(directory);
        }

        private void BackupDirectory(string candidateDirectory)
        {
            var modelDirectory = FaceToFaceBaseTransactionPathGuard
                .EnsureSafeProjectModelDescendant(_projectPath, candidateDirectory);
            if (_directoryBackups.Keys.Any(directory => BridgeGenerationFileBackup.Same(directory, modelDirectory) ||
                BridgeGenerationFileBackup.Under(directory, modelDirectory)))
            {
                return;
            }

            if (!Directory.Exists(modelDirectory))
            {
                throw new DirectoryNotFoundException("待备份基座目录不存在：" + modelDirectory);
            }

            Directory.CreateDirectory(_transactionRoot);
            var backupDirectory = Path.Combine(
                _transactionRoot,
                "模型备份",
                _directoryBackups.Count.ToString("D2", CultureInfo.InvariantCulture));
            CopyDirectory(modelDirectory, backupDirectory);
            _directoryBackups.Add(modelDirectory, backupDirectory);
        }

        public void RegisterNewDirectory(string directoryPath)
        {
            var safePath = FaceToFaceBaseTransactionPathGuard
                .EnsureSafeProjectModelDescendant(_projectPath, directoryPath);
            if (Directory.Exists(safePath))
            {
                throw new IOException("新增模型目录已存在，拒绝登记为事务新建目录：" + safePath);
            }

            _newDirectories.Add(safePath);
        }

        public void RegisterCreatedFiles(IEnumerable<string> filePaths)
        {
            if (filePaths == null)
            {
                throw new ArgumentNullException(nameof(filePaths));
            }

            foreach (var filePath in filePaths)
            {
                var fullPath = Path.GetFullPath(filePath);
                FaceToFaceBaseTransactionPathGuard.EnsureSafeProjectModelDescendant(
                    _projectPath,
                    fullPath);
                _createdFiles.Add(fullPath);
            }
        }

        public FaceToFaceBaseNameNormalizationResult NormalizeExistingModelNames(
            string modelPath,
            string templateAssemblyPath,
            string targetSuffix)
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                throw new FileNotFoundException("待归一名称的基座模型不存在：" + modelPath, modelPath);
            }

            if (string.IsNullOrWhiteSpace(templateAssemblyPath)
                || !File.Exists(templateAssemblyPath))
            {
                throw new FileNotFoundException(
                    "名称归一缺少对应模板装配体：" + templateAssemblyPath,
                    templateAssemblyPath);
            }

            var sourceModelPath = Path.GetFullPath(modelPath);
            var modelDirectory = FaceToFaceBaseTransactionPathGuard.EnsureSafeDescendant(
                _baseRoot,
                Path.GetDirectoryName(sourceModelPath));
            var currentSuffix = FaceToFaceBaseFileNamePolicy.ResolveCurrentSuffix(
                Path.GetFileNameWithoutExtension(templateAssemblyPath),
                Path.GetFileNameWithoutExtension(sourceModelPath));
            var driver = new SolidWorksDriver(_swApp);
            IModelDoc2 model = null;
            ModelDocExtension extension = null;
            PackAndGo packAndGo = null;
            try
            {
                model = driver.OpenOrActivateDocument(sourceModelPath);
                extension = model?.Extension as ModelDocExtension;
                packAndGo = extension?.GetPackAndGo() as PackAndGo;
                if (model == null || extension == null || packAndGo == null)
                {
                    throw new InvalidOperationException(
                        "无法创建基座名称归一 PackAndGo：" + sourceModelPath);
                }

                packAndGo.IncludeDrawings = true;
                packAndGo.IncludeSimulationResults = false;
                packAndGo.IncludeToolboxComponents = false;
                packAndGo.FlattenToSingleFolder = true;
                if (!packAndGo.SetSaveToName(true, modelDirectory))
                {
                    throw new InvalidOperationException(
                        "PackAndGo 无法设置名称归一目标目录：" + modelDirectory);
                }

                object rawNames;
                object rawStatuses;
                if (!packAndGo.GetDocumentSaveToNames(out rawNames, out rawStatuses))
                {
                    throw new InvalidOperationException("PackAndGo 无法读取基座物理文档列表。");
                }

                var allNames = rawNames as string[];
                if (allNames == null || allNames.Length == 0)
                {
                    throw new InvalidOperationException("PackAndGo 返回的基座物理文档列表为空。");
                }

                var statusArray = rawStatuses as Array;
                var physicalIndexes = Enumerable.Range(0, allNames.Length)
                    .Where(index => !IsVirtualDocument(allNames[index], statusArray, index))
                    .ToArray();
                var physicalNames = physicalIndexes.Select(index => allNames[index]).ToArray();
                var normalization = FaceToFaceBaseNameNormalizationPolicy.Create(
                    modelDirectory,
                    sourceModelPath,
                    physicalNames,
                    currentSuffix,
                    targetSuffix,
                    File.Exists);

                var destinationBySource = normalization.SourceFiles.ToDictionary(
                    source => source,
                    source => Path.Combine(
                        modelDirectory,
                        FaceToFaceBaseFileNamePolicy.ReplaceSuffix(
                            Path.GetFileNameWithoutExtension(source),
                            currentSuffix,
                            targetSuffix)
                            + Path.GetExtension(source)),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var index in physicalIndexes)
                {
                    allNames[index] = destinationBySource[Path.GetFullPath(allNames[index])];
                }

                if (!packAndGo.SetDocumentSaveToNames(allNames))
                {
                    throw new InvalidOperationException("PackAndGo 无法应用基座名称归一目标列表。");
                }

                var saveStatuses = extension.SavePackAndGo(packAndGo) as int[];
                if (saveStatuses == null
                    || saveStatuses.Any(status =>
                        status != (int)swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_Succeed
                        && status != (int)swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_SaveToEmpty))
                {
                    throw new IOException("PackAndGo 名称归一返回失败状态：" + sourceModelPath);
                }

                var missing = normalization.CreatedFiles.Where(path => !File.Exists(path)).ToArray();
                if (missing.Length > 0)
                {
                    throw new FileNotFoundException(
                        "PackAndGo 名称归一后存在未落盘文件：" + string.Join("；", missing),
                        missing[0]);
                }

                return new FaceToFaceBaseNameNormalizationResult(
                    normalization.TargetModelPath,
                    normalization.CreatedFiles,
                    normalization.ObsoleteFiles);
            }
            finally
            {
                SolidWorksFileHelper.ReleaseComReference(packAndGo);
                SolidWorksFileHelper.ReleaseComReference(extension);
                if (model != null)
                {
                    try
                    {
                        driver.TryCloseDocumentByPath(sourceModelPath);
                        driver.CloseOpenDocumentsUnderDirectory(
                            modelDirectory,
                            new[] { _totalAssemblyPath });
                    }
                    finally
                    {
                        SolidWorksFileHelper.ReleaseComReference(model);
                    }
                }

                EnsureTotalAssemblyReady();
            }
        }

        public FaceToFaceBaseFirstFrameAnchor DetachFirstFrameAnchor(
            FaceToFaceBasePhysicalModelSnapshot ordinaryBase)
        {
            if (ordinaryBase == null)
            {
                throw new ArgumentNullException(nameof(ordinaryBase));
            }

            if (ordinaryBase.Kind != FaceToFaceBaseModelKind.Base)
            {
                throw new ArgumentException("首基架锚点只能从普通基座上解除。", nameof(ordinaryBase));
            }

            EnsureTotalAssemblyReady();
            var component = FindRootComponent(ordinaryBase.ComponentFullName);
            if (component == null)
            {
                throw new InvalidOperationException(
                    "解除首基架锚点失败，未找到普通基座组件："
                    + ordinaryBase.ComponentFullName);
            }

            var matches = new List<Tuple<string, FaceToFaceBaseFirstFrameAnchor>>();
            foreach (var mateObject in (component.GetMates() as object[]) ?? Array.Empty<object>())
            {
                var mate = mateObject as IMate2;
                if (mate == null)
                {
                    continue;
                }

                var anchor = ResolveFirstFrameAnchor(
                    ordinaryBase.PositionName,
                    component.Name2,
                    ReadMateEndpoints(mate));
                if (anchor == null)
                {
                    continue;
                }

                var mateName = TryGetMateFeatureName(mateObject);
                if (string.IsNullOrWhiteSpace(mateName))
                {
                    throw new InvalidOperationException(
                        "已识别普通基座与首基架锚点，但无法取得配合特征名称："
                        + ordinaryBase.ComponentFullName);
                }

                matches.Add(Tuple.Create(mateName, anchor));
            }

            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    "普通基座存在多条首基架锚点配合，无法安全替换："
                    + ordinaryBase.ComponentFullName);
            }

            if (matches.Count == 0)
            {
                return null;
            }

            var match = matches[0];
            SWHelper.TryDeleteAssemblyMatesByName(
                _totalAssemblyModel,
                new[] { match.Item1 });

            var currentComponent = FindRootComponent(ordinaryBase.ComponentFullName);
            if (currentComponent == null)
            {
                throw new InvalidOperationException(
                    "删除首基架锚点配合后，无法重新读取普通基座组件："
                    + ordinaryBase.ComponentFullName);
            }

            var remainingMateEndpointSets = ((currentComponent.GetMates() as object[])
                    ?? Array.Empty<object>())
                .Select(item => item as IMate2)
                .Where(mate => mate != null)
                .Select(ReadMateEndpoints)
                .ToArray();
            if (HasFirstFrameAnchor(
                ordinaryBase.PositionName,
                currentComponent.Name2,
                remainingMateEndpointSets))
            {
                throw new InvalidOperationException("删除首基架锚点配合失败：" + match.Item1);
            }

            return match.Item2;
        }

        public void RestoreFirstFrameAnchor(
            FaceToFaceBaseAssemblyToken ordinaryBase,
            FaceToFaceBaseFirstFrameAnchor anchor)
        {
            if (ordinaryBase == null)
            {
                throw new ArgumentNullException(nameof(ordinaryBase));
            }

            if (anchor == null)
            {
                throw new ArgumentNullException(nameof(anchor));
            }

            if (ordinaryBase.Kind != FaceToFaceBaseModelKind.Base
                || !string.Equals(
                    ordinaryBase.PositionName,
                    anchor.PositionName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "恢复首基架锚点时，普通基座类型或位置与已保存锚点不一致。");
            }

            EnsureTotalAssemblyReady();
            var ordinaryComponent = FindRootComponent(ordinaryBase.ComponentFullName);
            if (ordinaryComponent == null)
            {
                throw new InvalidOperationException(
                    "恢复首基架锚点失败，未找到新普通基座组件："
                    + ordinaryBase.ComponentFullName);
            }

            var frameComponent = FindRootComponent(anchor.FrameComponentFullName);
            if (frameComponent == null)
            {
                throw new InvalidOperationException(
                    "恢复首基架锚点失败，未找到原首基架组件："
                    + anchor.FrameComponentFullName);
            }

            try
            {
                _totalAssembly.ResolveAllLightWeightComponents(false);
            }
            catch
            {
                // 解析失败时仍尝试恢复坐标系配合；后续端点校验会给出确定结果。
            }

            if (!PublicMethod.AddMateUseCoord(
                _swApp,
                _totalAssembly,
                ordinaryComponent,
                frameComponent,
                FaceToFaceFrameChainAnalyzer.BaseInstallationCoordinateName,
                anchor.FrameCoordinateName))
            {
                throw new InvalidOperationException(
                    $"恢复{anchor.PositionName}普通基座与首基架配合失败："
                    + $"基架={anchor.FrameComponentFullName}，坐标系={anchor.FrameCoordinateName}。");
            }

            FaceToFaceBaseOperationGuard.EnsureRebuildSucceeded(
                _totalAssemblyModel.EditRebuild3(),
                anchor.PositionName + "首基架锚点恢复",
                _totalAssemblyPath);

            var mateEndpointSets = ((ordinaryComponent.GetMates() as object[])
                    ?? Array.Empty<object>())
                .Select(item => item as IMate2)
                .Where(mate => mate != null)
                .Select(ReadMateEndpoints)
                .ToArray();
            if (!PositioningMateSetIsExact(
                mateEndpointSets,
                frameComponent.Name2,
                anchor.FrameCoordinateName,
                ordinaryComponent.Name2,
                FaceToFaceFrameChainAnalyzer.BaseInstallationCoordinateName))
            {
                throw new InvalidOperationException(
                    $"恢复{anchor.PositionName}普通基座与首基架配合后，端点校验失败。");
            }
        }

        public void DeletePositioningMates(FaceToFaceBasePhysicalModelSnapshot model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            CreateAssembler().DeletePositioningMates(model.ComponentFullName, model.Kind);
        }

        public void RemoveRootComponent(string componentFullName)
        {
            CreateAssembler().DeleteRootComponent(componentFullName);
        }

        public FaceToFaceBaseAssemblyToken InsertAtRoot(
            string positionName,
            FaceToFaceBaseModelKind kind,
            string modelPath,
            string rootCoordinateName)
        {
            return CreateAssembler().InsertAtRoot(
                positionName,
                kind,
                modelPath,
                rootCoordinateName);
        }

        public FaceToFaceBaseAssemblyToken InsertBaseOnElevated(
            string positionName,
            string modelPath,
            FaceToFaceBaseAssemblyToken elevated)
        {
            return CreateAssembler().InsertBaseOnElevated(positionName, modelPath, elevated);
        }

        public void WriteMapping(string mappingJson)
        {
            if (string.IsNullOrWhiteSpace(mappingJson))
            {
                throw new ArgumentException("模型对照映射不能为空。", nameof(mappingJson));
            }

            EnsureTotalAssemblyReady();
            new SolidWorksDriver(_swApp).SetCustomProperty(
                _totalAssemblyModel,
                FaceToFaceBaseModelMappingHelper.PropertyName,
                mappingJson);
        }

        public FaceToFaceBaseSecondarySnapshot ValidateFinalState(
            CabinetDriverParameterSnapshot parameters)
        {
            _insulatorRecovery?.CompleteInAssembly(_totalAssemblyModel);
            var snapshot = CaptureCurrentSnapshot(parameters);
            new SolidWorksParentDimensionDriver(_swApp, path => BridgeGenerationFileBackup.Under(_projectPath, path))
                .VerifySharedBaseDimensions(_totalAssemblyModel, snapshot.Models.Where(model =>
                    SolidWorksBaseBridgeAngleDriver.IsOrdinaryBasePath(model.ModelPath)).Select(model => FindRootComponent(model.ComponentFullName)));
            return snapshot;
        }

        public void RebuildAndSaveTotalAssembly()
        {
            SaveTotalAssembly(rebuild: true);
        }

        public void RebuildTotalAssemblyAfterDetach()
        {
            EnsureTotalAssemblyReady();
            // 非新增增高基座场景只刷新内存引用树，不保存尚未回装的中间状态。
            FaceToFaceBaseOperationGuard.EnsureRebuildSucceeded(
                _totalAssemblyModel.EditRebuild3(),
                "基座模板预拆卸总装刷新",
                _totalAssemblyPath);
        }

        public IReadOnlyCollection<string> GetReferencedBaseModelPaths()
        {
            EnsureTotalAssemblyReady();
            var paths = new List<string>();
            // false 会同时枚举根组件和子组件，回滚清理时必须看见基座内部的绝缘子引用。
            var components = _totalAssembly.GetComponents(false) as object[];
            if (components == null)
            {
                throw new InvalidOperationException("无法枚举总装配体组件，不能确认待清理模型为零引用。");
            }

            foreach (var item in components)
            {
                var component = item as Component2;
                if (component == null)
                {
                    throw new InvalidOperationException("总装配体包含无法识别的组件，不能确认待清理模型为零引用。");
                }

                string path;
                try
                {
                    path = component.GetPathName();
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "读取总装配体组件路径失败，不能确认待清理模型为零引用。",
                        exception);
                }

                paths.Add(path);
            }

            return EnsureCompleteReferencePaths(paths);
        }

        internal static IReadOnlyCollection<string> EnsureCompleteReferencePaths(
            IEnumerable<string> paths)
        {
            if (paths == null)
            {
                throw new InvalidOperationException("总装配体组件引用集合不可用。");
            }

            var completePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new InvalidOperationException(
                        "存在无法读取路径的总装配体组件，不能确认待清理模型为零引用。");
                }

                completePaths.Add(Path.GetFullPath(path));
            }

            return completePaths.ToArray();
        }

        internal static void EnsureExpectedActiveAssembly(
            string expectedAssemblyPath,
            string activeDocumentPath,
            bool activeDocumentIsAssembly)
        {
            if (string.IsNullOrWhiteSpace(expectedAssemblyPath))
            {
                throw new ArgumentException("目标总装路径不能为空。", nameof(expectedAssemblyPath));
            }

            var expectedPath = Path.GetFullPath(expectedAssemblyPath);
            var actualPath = string.IsNullOrWhiteSpace(activeDocumentPath)
                ? null
                : Path.GetFullPath(activeDocumentPath);
            if (!activeDocumentIsAssembly
                || string.IsNullOrWhiteSpace(actualPath)
                || !string.Equals(
                    expectedPath,
                    actualPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "面对面基座二次驱动未处于目标总装活动上下文。"
                    + $"期望={expectedPath}；实际={actualPath ?? "<无活动文档>"}。");
            }
        }

        internal static string BuildRebuildFailureDetails(
            string activeDocumentPath,
            int needsRebuildStatus,
            IEnumerable<string> featureIssues)
        {
            var issues = (featureIssues ?? Enumerable.Empty<string>())
                .Where(issue => !string.IsNullOrWhiteSpace(issue))
                .Take(20)
                .ToArray();
            var lines = new List<string>
            {
                "活动文档=" + (string.IsNullOrWhiteSpace(activeDocumentPath)
                    ? "<无活动文档>"
                    : activeDocumentPath),
                "NeedsRebuild2=" + needsRebuildStatus
            };
            if (issues.Length == 0)
            {
                lines.Add("错误特征=<未枚举到具有错误码的特征>");
            }
            else
            {
                lines.Add("错误特征：");
                lines.AddRange(issues.Select(issue => "- " + issue));
            }

            return string.Join(System.Environment.NewLine, lines);
        }

        public void CloseModelDocuments(string directoryPath)
        {
            var safeDirectory = FaceToFaceBaseTransactionPathGuard
                .EnsureSafeProjectModelDescendant(_projectPath, directoryPath);
            // 总装必须继续保持打开，此处只关闭已从总装解除引用的基座/零件文档。
            new SolidWorksDriver(_swApp).CloseOpenDocumentsUnderDirectoryConfirmed(
                safeDirectory,
                new[] { _totalAssemblyPath });
        }

        public void CloseModelDocument(string modelPath)
        {
            var safePath = FaceToFaceBaseTransactionPathGuard
                .EnsureSafeProjectModelOrTemplate(
                    _projectPath,
                    modelPath,
                    new[]
                    {
                        _templateCopier.GetTemplateAssemblyPath(FaceToFaceBaseModelKind.Base),
                        _templateCopier.GetTemplateAssemblyPath(
                            FaceToFaceBaseModelKind.ElevatedBase)
                    });
            new SolidWorksDriver(_swApp).CloseDocumentByPathConfirmed(safePath);
        }

        public void RestoreBaselineAndBackups()
        {
            var errors = new List<string>();
            // 任何关闭未确认时立即停止，不覆盖仍可能被 SolidWorks 引用的磁盘模型。
            CloseTotalAssemblyForRollback();
            CloseDocumentsUnderTransactionModelRoots();
            // 较早备份的精确子目录最后恢复，避免随后发现的祖先目录覆盖其基线。
            foreach (var backup in _directoryBackups.OrderBy(pair => pair.Key.Length))
            {
                try
                {
                    var original = FaceToFaceBaseTransactionPathGuard
                        .EnsureSafeProjectModelDescendant(_projectPath, backup.Key);
                    if (Directory.Exists(original))
                    {
                        Directory.Delete(original, true);
                    }

                    CopyDirectory(backup.Value, original);
                }
                catch (Exception exception)
                {
                    errors.Add("恢复模型备份失败：" + backup.Key + "；" + exception.Message);
                }
            }

            try
            {
                RestoreTotalAssemblyBaseline();
            }
            catch (Exception exception)
            {
                errors.Add("恢复总装配体基线失败：" + exception.Message);
            }

            TryRemoveRegisteredOutputs(errors);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join(System.Environment.NewLine, errors));
            }
        }

        public void ClearTransactionArtifacts()
        {
            if (!Directory.Exists(_transactionRoot))
            {
                return;
            }

            FaceToFaceBaseTransactionPathGuard.EnsureSafeDescendant(
                _baseRoot,
                _transactionRoot);
            Directory.Delete(_transactionRoot, true);
        }

        private FaceToFaceBasePhysicalModelSnapshot CapturePhysicalModel(
            string mappingJson,
            FaceToFaceBasePositionPlan position,
            FaceToFaceBaseModelKind kind)
        {
            string componentFullName;
            if (!FaceToFaceBaseModelMappingHelper.TryGetComponentFullName(
                mappingJson,
                position.PositionName,
                kind == FaceToFaceBaseModelKind.ElevatedBase,
                out componentFullName))
            {
                throw new InvalidOperationException(
                    $"模型对照映射缺少{position.PositionName}/{kind}组件。");
            }

            var component = FindRootComponent(componentFullName);
            if (component == null)
            {
                throw new InvalidOperationException(
                    "模型对照映射指向的根级组件不存在：" + componentFullName);
            }

            var modelPath = component.GetPathName();
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                throw new FileNotFoundException(
                    "基座根级组件模型不存在：" + modelPath,
                    modelPath);
            }

            var modelPlan = kind == FaceToFaceBaseModelKind.Base
                ? position.BaseModel
                : position.ElevatedBaseModel;
            var hostedPlan = position.Insulator != null
                && position.Insulator.TargetBaseKind == kind
                ? position.Insulator
                : null;
            var model = component.GetModelDoc2() as IModelDoc2;
            if (model == null)
            {
                throw new InvalidOperationException(
                    $"无法读取{position.PositionName}/{kind}实际模型参数：{modelPath}");
            }

            EnsureModelGlobalVariables(model, modelPlan.GlobalVariables, modelPath);
            var hosted = FindHostedInsulator(model);
            ValidateHostedInsulator(position, kind, modelPlan, hostedPlan, model, hosted);
            var connectionKind = ValidatePositioningMate(
                mappingJson,
                position,
                kind,
                component);
            var templatePath = _templateCopier.GetTemplateAssemblyPath(kind);
            var templateName = Path.GetFileNameWithoutExtension(templatePath);
            var currentName = Path.GetFileNameWithoutExtension(modelPath);
            var suffix = FaceToFaceBaseFileNamePolicy.ResolveCurrentSuffix(
                templateName,
                currentName);
            return new FaceToFaceBasePhysicalModelSnapshot(
                position.PositionName,
                kind,
                componentFullName,
                modelPath,
                hosted?.ModelPath,
                FaceToFaceBaseModelIdentity.FromPlan(modelPlan, hostedPlan),
                new FaceToFaceBaseModelNameState(
                    templateName,
                    currentName,
                    suffix),
                connectionKind);
        }

        private HostedInsulatorState FindHostedInsulator(IModelDoc2 hostModel)
        {
            if (hostModel is AssemblyDoc hostAssembly)
            {
                try { hostAssembly.ResolveAllLightWeightComponents(false); } catch { }
            }

            var rootComponent = hostModel.ConfigurationManager?
                .ActiveConfiguration?
                .GetRootComponent3(true) as IComponent2;
            var candidates = CollectFirstLevelHostedInsulatorCandidates(
                rootComponent,
                parent => (parent.GetChildren() as object[] ?? Array.Empty<object>())
                    .OfType<IComponent2>(),
                candidate => candidate.Name2);
            // 快照只允许一个宿主绝缘子；严格拒绝替换后遗留的重复组件。
            var component = ResolveSingleHostedInsulatorCandidate(candidates);
            if (component == null)
            {
                return null;
            }

            bool suppressed;
            try { suppressed = component.IsSuppressed(); }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "宿主基座内的绝缘子组件压缩状态无法读取。",
                    exception);
            }

            if (suppressed)
            {
                throw new InvalidOperationException(
                    "宿主基座内存在已压缩的绝缘子残留组件。");
            }

            var path = component.GetPathName();
            var model = component.GetModelDoc2() as IModelDoc2;
            if (string.IsNullOrWhiteSpace(path)
                || !File.Exists(path)
                || model == null
                || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new FileNotFoundException(
                    "宿主基座内的绝缘子模型无法完整读取：" + path,
                    path);
            }

            return new HostedInsulatorState(Path.GetFullPath(path), model);
        }

        internal static IReadOnlyList<T> CollectFirstLevelHostedInsulatorCandidates<T>(
            T root,
            Func<T, IEnumerable<T>> childSelector,
            Func<T, string> nameSelector)
            where T : class
        {
            // 实现迁入未加密的界面运行时，保留原调用入口。
            return BridgeModelReadOperations.CollectFirstLevelHostedInsulatorCandidates(root, childSelector, nameSelector);
        }

        private static bool IsHostedInsulatorComponentName(string componentName)
        {
            // 实现迁入未加密的界面运行时，保留原调用入口。
            return BridgeModelReadOperations.IsHostedInsulatorComponentName(componentName);
        }

        internal static T ResolveSingleHostedInsulatorCandidate<T>(IEnumerable<T> candidates)
            where T : class
        {
            // 实现迁入未加密的界面运行时，保留原调用入口。
            return BridgeModelReadOperations.ResolveSingleHostedInsulatorCandidate(candidates);
        }

        private void ValidatePositionStructure(
            string mappingJson,
            IReadOnlyList<FaceToFaceBasePositionPlan> plans)
        {
            var enabled = plans.ToDictionary(x => x.PositionName, StringComparer.Ordinal);
            foreach (var position in _orderedPositions)
            {
                string baseComponent;
                string elevatedComponent;
                var hasBase = FaceToFaceBaseModelMappingHelper.TryGetComponentFullName(
                    mappingJson, position, false, out baseComponent);
                var hasElevated = FaceToFaceBaseModelMappingHelper.TryGetComponentFullName(
                    mappingJson, position, true, out elevatedComponent);
                FaceToFaceBasePositionPlan plan;
                if (!enabled.TryGetValue(position, out plan))
                {
                    if (hasBase || hasElevated)
                    {
                        throw new InvalidOperationException(
                            "已停用位置仍在模型对照映射中引用基座：" + position);
                    }

                    continue;
                }

                if (!hasBase || hasElevated != plan.HasElevatedBase)
                {
                    throw new InvalidOperationException(
                        $"{position}的普通/增高基座实际结构与参数快照不一致。");
                }
            }
        }

        private void EnsureModelGlobalVariables(
            IModelDoc2 model,
            IReadOnlyDictionary<string, object> expected,
            string modelIdentity)
        {
            var actual = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in expected.Keys)
            {
                double value;
                if (!CabinetAssemblyDimensionReader.TryGetGlobalVariableValue(
                    model,
                    variable,
                    out value))
                {
                    throw new InvalidOperationException(
                        $"模型“{modelIdentity}”缺少可读取的全局变量“{CabinetHoleParameters.GetParameterDisplayName(variable)}”。");
                }

                actual.Add(variable, value);
            }

            EnsureExpectedGlobalVariables(expected, actual, modelIdentity);
        }

        internal static void EnsureExpectedGlobalVariables(
            IReadOnlyDictionary<string, object> expected,
            IReadOnlyDictionary<string, double> actual,
            string modelIdentity)
        {
            foreach (var variable in expected)
            {
                if (!actual.ContainsKey(variable.Key))
                {
                    throw new InvalidOperationException(
                        $"模型“{modelIdentity}”缺少可读取的全局变量“{CabinetHoleParameters.GetParameterDisplayName(variable.Key)}”。");
                }
            }
        }

        private void ValidateHostedInsulator(
            FaceToFaceBasePositionPlan position,
            FaceToFaceBaseModelKind kind,
            FaceToFaceBaseModelPlan modelPlan,
            FaceToFaceBaseInsulatorPlan expected,
            IModelDoc2 hostModel,
            HostedInsulatorState actual)
        {
            if (expected == null)
            {
                if (actual != null)
                {
                    throw new InvalidOperationException(
                        $"{position.PositionName}/{kind}实际存在快照未登记的绝缘子引用。");
                }

                return;
            }

            if (actual == null)
            {
                throw new InvalidOperationException(
                    $"{position.PositionName}/{kind}缺少快照要求的绝缘子引用。");
            }

            FaceToFaceBaseTransactionPathGuard.EnsureSafeProjectModelDescendant(
                _projectPath,
                actual.ModelPath);
            // 未达标尺寸由本次恢复清单保留到最终提示；组件身份、安装定位仍须通过。
            if (_insulatorRecovery?.Contains(actual.ModelPath) != true)
            {
                var actualRootCount = InsulatorDriver.ReadSinglePhaseRootCount(actual.Model);
                if (actualRootCount != expected.SinglePhaseRootCount)
                {
                    throw new InvalidOperationException(
                        $"{position.PositionName}/{kind}绝缘子实际单相根数与快照不一致。");
                }

                EnsureModelGlobalVariables(actual.Model, expected.GlobalVariables, actual.ModelPath);
            }
            var dimension = hostModel.Parameter(expected.PlacementDimensionName) as IDimension;
            if (dimension == null
                || NormalizeMillimeter(dimension.SystemValue * 1000d)
                    != NormalizeMillimeter(expected.PlacementHeight))
            {
                throw new InvalidOperationException(
                    $"{position.PositionName}/{kind}绝缘子实际定位与快照不一致。");
            }
        }

        private FaceToFaceBaseConnectionKind ValidatePositioningMate(
            string mappingJson,
            FaceToFaceBasePositionPlan position,
            FaceToFaceBaseModelKind kind,
            Component2 component)
        {
            string parentComponentFullName = null;
            FaceToFaceBaseMateRoute route;
            FaceToFaceBaseConnectionKind connectionKind;
            if (kind == FaceToFaceBaseModelKind.Base && position.HasElevatedBase)
            {
                if (!FaceToFaceBaseModelMappingHelper.TryGetComponentFullName(
                    mappingJson,
                    position.PositionName,
                    true,
                    out parentComponentFullName))
                {
                    throw new InvalidOperationException(
                        position.PositionName + "缺少增高基座映射，无法验证叠装配合。");
                }

                route = FaceToFaceBaseAssembler.ResolveStackedMateRoute();
                connectionKind = FaceToFaceBaseConnectionKind.StackedOnElevatedBase;
            }
            else
            {
                route = FaceToFaceBaseAssembler.ResolveRootMateRoute(
                    position.RootCoordinateName,
                    kind);
                connectionKind = FaceToFaceBaseConnectionKind.Root;
            }

            var mateEndpointSets = new List<IReadOnlyCollection<Tuple<string, string>>>();
            var mates = component.GetMates() as object[];
            if (mates != null)
            {
                foreach (var mateObject in mates)
                {
                    var mate = mateObject as IMate2;
                    if (mate == null)
                    {
                        continue;
                    }

                    mateEndpointSets.Add(ReadMateEndpoints(mate));
                }
            }

            // 目标子坐标系必须只参与这一条定位配合，避免旧配合残留造成过约束。
            if (!PositioningMateSetIsExact(
                mateEndpointSets,
                parentComponentFullName,
                route.ParentCoordinateName,
                component.Name2,
                route.ChildCoordinateName))
            {
                throw new InvalidOperationException(
                    $"{position.PositionName}/{kind}实际定位配合缺失、冲突或存在残留配合。");
            }

            return connectionKind;
        }

        private IReadOnlyCollection<Tuple<string, string>> ReadMateEndpoints(IMate2 mate)
        {
            var endpoints = new List<Tuple<string, string>>();
            var count = mate.GetMateEntityCount();
            for (var index = 0; index < count; index++)
            {
                var entity = mate.MateEntity(index) as MateEntity2;
                if (entity == null)
                {
                    continue;
                }

                var referenceComponent = entity.ReferenceComponent;
                endpoints.Add(Tuple.Create(
                    SolidWorksFaceToFaceFrameReadOperations.NormalizeMateComponentInstanceId(
                        referenceComponent?.Name2,
                        referenceComponent?.GetPathName(),
                        _totalAssemblyPath),
                    (entity.Reference as IFeature)?.Name));
            }

            return endpoints;
        }

        internal static FaceToFaceBaseFirstFrameAnchor ResolveFirstFrameAnchor(
            string positionName,
            string ordinaryBaseComponentFullName,
            IEnumerable<Tuple<string, string>> endpoints)
        {
            if (endpoints == null)
            {
                throw new ArgumentNullException(nameof(endpoints));
            }

            var values = endpoints.Where(endpoint => endpoint != null).ToArray();
            var baseEndpoints = values.Where(endpoint => EndpointMatches(
                endpoint,
                ordinaryBaseComponentFullName,
                FaceToFaceFrameChainAnalyzer.BaseInstallationCoordinateName)).ToArray();
            if (baseEndpoints.Length == 0)
            {
                return null;
            }

            var frameEndpoints = values.Where(endpoint =>
                !string.IsNullOrWhiteSpace(endpoint.Item1)
                && !FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(
                    endpoint.Item1,
                    ordinaryBaseComponentFullName)
                && IsFirstFrameInletCoordinateName(endpoint.Item2)).ToArray();
            if (baseEndpoints.Length != 1
                || frameEndpoints.Length != 1
                || values.Length != 2)
            {
                throw new InvalidOperationException(
                    "普通基座的“基座安装坐标系”配合端点不完整或不唯一，无法安全保存首基架锚点。");
            }

            return new FaceToFaceBaseFirstFrameAnchor(
                positionName,
                frameEndpoints[0].Item1,
                frameEndpoints[0].Item2);
        }

        internal static bool IsFirstFrameInletCoordinateName(string coordinateName)
        {
            return string.Equals(
                    coordinateName,
                    FaceToFaceFrameChainAnalyzer.InletCoordinate1,
                    StringComparison.Ordinal)
                || string.Equals(
                    coordinateName,
                    FaceToFaceFrameChainAnalyzer.InletCoordinate2,
                    StringComparison.Ordinal);
        }

        internal static bool HasFirstFrameAnchor(
            string positionName,
            string ordinaryBaseComponentFullName,
            IEnumerable<IEnumerable<Tuple<string, string>>> mateEndpointSets)
        {
            if (mateEndpointSets == null)
            {
                throw new ArgumentNullException(nameof(mateEndpointSets));
            }

            return mateEndpointSets
                .Where(endpoints => endpoints != null)
                .Any(endpoints => ResolveFirstFrameAnchor(
                    positionName,
                    ordinaryBaseComponentFullName,
                    endpoints) != null);
        }

        internal static bool MateEndpointsMatch(
            IEnumerable<Tuple<string, string>> endpoints,
            string expectedParentComponent,
            string expectedParentCoordinate,
            string expectedChildComponent,
            string expectedChildCoordinate)
        {
            // 实现迁入未加密的界面运行时，保留原调用入口。
            return BridgeModelReadOperations.MateEndpointsMatch(endpoints, expectedParentComponent, expectedParentCoordinate, expectedChildComponent, expectedChildCoordinate);
        }

        internal static bool PositioningMateSetIsExact(
            IEnumerable<IEnumerable<Tuple<string, string>>> mateEndpointSets,
            string expectedParentComponent,
            string expectedParentCoordinate,
            string expectedChildComponent,
            string expectedChildCoordinate)
        {
            // 实现迁入未加密的界面运行时，保留原调用入口。
            return BridgeModelReadOperations.PositioningMateSetIsExact(mateEndpointSets, expectedParentComponent, expectedParentCoordinate, expectedChildComponent, expectedChildCoordinate);
        }

        private static bool EndpointMatches(
            Tuple<string, string> endpoint,
            string expectedComponent,
            string expectedCoordinate)
        {
            // 实现迁入未加密的界面运行时，保留原调用入口。
            return BridgeModelReadOperations.EndpointMatches(endpoint, expectedComponent, expectedCoordinate);
        }

        private static string TryGetMateFeatureName(object mateObject)
        {
            if (mateObject == null)
            {
                return null;
            }

            try
            {
                var concreteFeature = mateObject as Feature;
                if (!string.IsNullOrWhiteSpace(concreteFeature?.Name))
                {
                    return concreteFeature.Name;
                }
            }
            catch
            {
                // 继续尝试 IFeature，兼容旧版 SolidWorks 互操作程序集。
            }

            try
            {
                var feature = mateObject as IFeature;
                return string.IsNullOrWhiteSpace(feature?.Name) ? null : feature.Name;
            }
            catch
            {
                return null;
            }
        }

        private static double NormalizeMillimeter(double value)
        {
            return Math.Round(value, 3, MidpointRounding.AwayFromZero);
        }

        private Component2 FindRootComponent(string componentFullName)
        {
            var configuration = _totalAssemblyModel.ConfigurationManager?.ActiveConfiguration;
            var root = configuration?.GetRootComponent3(true);
            var children = root?.GetChildren() as object[];
            return children?
                .OfType<Component2>()
                .SingleOrDefault(component =>
                    FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(
                        component.Name2,
                        componentFullName));
        }

        private FaceToFaceBaseAssembler CreateAssembler()
        {
            EnsureTotalAssemblyReady();
            return new FaceToFaceBaseAssembler(_swApp, _totalAssemblyModel);
        }

        private void SetTotalAssembly(IModelDoc2 model)
        {
            _totalAssemblyModel = model ?? throw new ArgumentNullException(nameof(model));
            _totalAssembly = model as AssemblyDoc;
            _totalAssemblyPath = model.GetPathName();
            if (_totalAssembly == null
                || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY
                || string.IsNullOrWhiteSpace(_totalAssemblyPath))
            {
                throw new InvalidOperationException("二次基座操作要求已保存并打开的总装配体。");
            }

            _totalAssemblyPath = Path.GetFullPath(_totalAssemblyPath);
        }

        private void EnsureTotalAssemblyReady()
        {
            var expectedTotalAssemblyPath = _totalAssemblyPath;
            var active = _swApp.GetOpenDocumentByName(_totalAssemblyPath) as IModelDoc2;
            if (active == null)
            {
                active = new SolidWorksDriver(_swApp).OpenOrActivateDocument(_totalAssemblyPath);
            }

            string resolvedDocumentPath = null;
            var resolvedDocumentIsAssembly = false;
            if (active != null)
            {
                try
                {
                    resolvedDocumentPath = active.GetPathName();
                    resolvedDocumentIsAssembly =
                        active.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
                }
                catch
                {
                    resolvedDocumentPath = null;
                    resolvedDocumentIsAssembly = false;
                }
            }

            EnsureExpectedActiveAssembly(
                expectedTotalAssemblyPath,
                resolvedDocumentPath,
                resolvedDocumentIsAssembly);

            if (!ReferenceEquals(active, _totalAssemblyModel))
            {
                SetTotalAssembly(active);
            }

            SWHelper.EnsureAssemblyEditingContext(_swApp, _totalAssemblyModel);

            var activeDocument = _swApp.IActiveDoc2 as IModelDoc2;
            string activeDocumentPath = null;
            var activeDocumentIsAssembly = false;
            if (activeDocument != null)
            {
                try
                {
                    activeDocumentPath = activeDocument.GetPathName();
                    activeDocumentIsAssembly =
                        activeDocument.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
                }
                catch
                {
                    activeDocumentPath = null;
                    activeDocumentIsAssembly = false;
                }
            }

            EnsureExpectedActiveAssembly(
                expectedTotalAssemblyPath,
                activeDocumentPath,
                activeDocumentIsAssembly);
            EnsureTotalAssemblyRootEditContext();
        }

        private void EnsureTotalAssemblyRootEditContext()
        {
            try
            {
                if (_totalAssemblyModel.IsEditingSelf())
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "无法确认面对面基座二次驱动的总装编辑目标："
                    + _totalAssemblyPath,
                    exception);
            }

            Component2 editTarget;
            try
            {
                editTarget = _totalAssembly.GetEditTargetComponent();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "无法确认面对面基座二次驱动已退出组件原位编辑："
                    + _totalAssemblyPath,
                    exception);
            }

            var editTargetName = editTarget == null
                ? "<无法读取编辑组件>"
                : SafeRead(
                    () => editTarget.Name2,
                    "<未命名组件>");
            throw new InvalidOperationException(
                "面对面基座二次驱动仍处于组件原位编辑，禁止重建总装。"
                + "目标总装=" + _totalAssemblyPath
                + "；编辑组件=" + editTargetName + "。");
        }

        private void SaveTotalAssembly(bool rebuild)
        {
            EnsureTotalAssemblyReady();
            if (rebuild)
            {
                var rebuildSucceeded = _totalAssemblyModel.EditRebuild3();
                FaceToFaceBaseOperationGuard.EnsureRebuildSucceeded(
                    rebuildSucceeded,
                    "基座二次驱动总装提交",
                    _totalAssemblyPath,
                    CaptureRebuildFailureDetails);
            }

            var errors = 0;
            var warnings = 0;
            var saved = _totalAssemblyModel.Save3(
                (int)(swSaveAsOptions_e.swSaveAsOptions_Silent
                    | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                ref errors,
                ref warnings);
            if (!saved || errors != 0)
            {
                throw new InvalidOperationException(
                    $"保存基座二次驱动总装失败：{_totalAssemblyPath}；errors={errors}；warnings={warnings}");
            }
        }

        private string CaptureRebuildFailureDetails()
        {
            var activeDocumentPath = string.Empty;
            var needsRebuildStatus = -1;
            var whatsWrongCount = -1;
            var featureIssues = new List<string>();

            try
            {
                var activeDocument = _swApp.IActiveDoc2 as IModelDoc2;
                activeDocumentPath = activeDocument?.GetPathName();
            }
            catch (Exception exception)
            {
                featureIssues.Add(
                    "读取活动文档失败=" + FormatDiagnosticException(exception));
            }

            ModelDocExtension extension = null;
            try
            {
                extension = _totalAssemblyModel.Extension;
                if (extension != null)
                {
                    needsRebuildStatus = extension.NeedsRebuild2;
                    whatsWrongCount = extension.GetWhatsWrongCount();
                }
            }
            catch (Exception exception)
            {
                featureIssues.Add(
                    "读取重建状态失败=" + FormatDiagnosticException(exception));
            }

            try
            {
                featureIssues.AddRange(
                    CaptureRebuildFeatureIssues(_totalAssemblyModel, extension));
            }
            catch (Exception exception)
            {
                featureIssues.Add(
                    "枚举错误特征失败=" + FormatDiagnosticException(exception));
            }

            return BuildRebuildFailureDetailsCore(
                activeDocumentPath,
                needsRebuildStatus,
                whatsWrongCount,
                featureIssues);
        }

        private static string BuildRebuildFailureDetailsCore(
            string activeDocumentPath,
            int needsRebuildStatus,
            int whatsWrongCount,
            IEnumerable<string> featureIssues)
        {
            var details = BuildRebuildFailureDetails(
                activeDocumentPath,
                needsRebuildStatus,
                featureIssues);
            if (whatsWrongCount < 0)
            {
                return details;
            }

            var lines = details.Split(new[] { System.Environment.NewLine }, StringSplitOptions.None)
                .ToList();
            lines.Insert(Math.Min(2, lines.Count), "WhatsWrongCount=" + whatsWrongCount);
            return string.Join(System.Environment.NewLine, lines);
        }

        private static IReadOnlyCollection<string> CaptureRebuildFeatureIssues(
            IModelDoc2 model,
            ModelDocExtension extension)
        {
            var issues = new List<string>();
            var actualIssueCount = 0;

            if (extension != null)
            {
                try
                {
                    object features = null;
                    object errorCodes = null;
                    object warnings = null;
                    var whatsWrongSucceeded = extension.GetWhatsWrong(
                        out features,
                        out errorCodes,
                        out warnings);
                    if (!whatsWrongSucceeded)
                    {
                        issues.Add("GetWhatsWrong返回false，已回退到特征树枚举。");
                    }
                    else
                    {
                        var featureArray = features as Array;
                        if (featureArray != null)
                        {
                            for (var index = featureArray.GetLowerBound(0);
                                 index <= featureArray.GetUpperBound(0)
                                     && actualIssueCount < 20;
                                 index++)
                            {
                                var feature = featureArray.GetValue(index) as IFeature;
                                if (feature == null)
                                {
                                    continue;
                                }

                                int? errorCode = TryReadArrayInt(errorCodes, index);
                                bool? isWarning = TryReadArrayBool(warnings, index);
                                var issue = FormatFeatureIssue(
                                    feature,
                                    errorCode,
                                    isWarning);
                                if (!string.IsNullOrWhiteSpace(issue))
                                {
                                    issues.Add(issue);
                                    actualIssueCount++;
                                }
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    issues.Add(
                        "GetWhatsWrong失败=" + FormatDiagnosticException(exception));
                }
            }

            if (actualIssueCount == 0 && model != null)
            {
                var visitedFeatureCount = 0;
                IFeature firstFeature = null;
                try
                {
                    firstFeature = model.FirstFeature() as IFeature;
                }
                catch (Exception exception)
                {
                    issues.Add(
                        "读取首特征失败=" + FormatDiagnosticException(exception));
                }

                CollectFeatureTreeIssues(
                    firstFeature,
                    false,
                    issues,
                    ref visitedFeatureCount,
                    0);
            }

            return issues
                .Where(issue => !string.IsNullOrWhiteSpace(issue))
                .Distinct(StringComparer.Ordinal)
                .Take(20)
                .ToArray();
        }

        private static void CollectFeatureTreeIssues(
            IFeature firstFeature,
            bool subFeatureSiblings,
            ICollection<string> issues,
            ref int visitedFeatureCount,
            int depth)
        {
            var current = firstFeature;
            while (current != null
                   && visitedFeatureCount < 2000
                   && issues.Count < 20
                   && depth < 64)
            {
                visitedFeatureCount++;
                var issue = FormatFeatureIssue(current, null, null);
                if (!string.IsNullOrWhiteSpace(issue))
                {
                    issues.Add(issue);
                }

                IFeature firstSubFeature = null;
                try
                {
                    firstSubFeature = current.GetFirstSubFeature() as IFeature;
                }
                catch
                {
                    firstSubFeature = null;
                }

                CollectFeatureTreeIssues(
                    firstSubFeature,
                    true,
                    issues,
                    ref visitedFeatureCount,
                    depth + 1);

                try
                {
                    current = subFeatureSiblings
                        ? current.GetNextSubFeature() as IFeature
                        : current.GetNextFeature() as IFeature;
                }
                catch
                {
                    current = null;
                }
            }
        }

        private static string FormatFeatureIssue(
            IFeature feature,
            int? suppliedErrorCode,
            bool? suppliedWarning)
        {
            if (feature == null)
            {
                return null;
            }

            var errorCode = suppliedErrorCode ?? 0;
            var isWarning = suppliedWarning ?? false;
            if (!suppliedErrorCode.HasValue || !suppliedWarning.HasValue)
            {
                try
                {
                    var featureWarning = false;
                    var featureErrorCode = feature.GetErrorCode2(out featureWarning);
                    if (!suppliedErrorCode.HasValue)
                    {
                        errorCode = featureErrorCode;
                    }

                    if (!suppliedWarning.HasValue)
                    {
                        isWarning = featureWarning;
                    }
                }
                catch
                {
                    if (!suppliedErrorCode.HasValue && !suppliedWarning.HasValue)
                    {
                        return null;
                    }
                }
            }

            if (errorCode == 0 && !isWarning)
            {
                return null;
            }

            var featureName = SafeRead(() => feature.Name, "<未命名特征>");
            var featureType = SafeRead(feature.GetTypeName2, "<未知类型>");
            var errorName = Enum.GetName(typeof(swFeatureError_e), errorCode);
            var suppressed = SafeRead(
                () => feature.IsSuppressed().ToString().ToLowerInvariant(),
                "unknown");
            var issue = featureName
                + " [" + featureType + "]"
                + " error=" + errorCode
                + (string.IsNullOrWhiteSpace(errorName) ? string.Empty : "(" + errorName + ")")
                + " warning=" + isWarning.ToString().ToLowerInvariant()
                + " suppressed=" + suppressed;

            IMate2 mate = null;
            try
            {
                mate = feature.GetSpecificFeature2() as IMate2;
            }
            catch
            {
                mate = null;
            }

            if (mate == null)
            {
                return issue;
            }

            var mateType = SafeRead(
                () => mate.Type.ToString(CultureInfo.InvariantCulture),
                "unknown");
            var mateTypeName = int.TryParse(
                mateType,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var mateTypeValue)
                ? Enum.GetName(typeof(swMateType_e), mateTypeValue)
                : null;
            var endpoints = CaptureMateEndpoints(mate);
            return issue
                + " mateType=" + mateType
                + (string.IsNullOrWhiteSpace(mateTypeName)
                    ? string.Empty
                    : "(" + mateTypeName + ")")
                + (endpoints.Count == 0
                    ? string.Empty
                    : " endpoints=[" + string.Join(" | ", endpoints) + "]");
        }

        private static IReadOnlyCollection<string> CaptureMateEndpoints(IMate2 mate)
        {
            var endpoints = new List<string>();
            int count;
            try
            {
                count = mate.GetMateEntityCount();
            }
            catch (Exception exception)
            {
                endpoints.Add("端点数量读取失败=" + FormatDiagnosticException(exception));
                return endpoints;
            }

            for (var index = 0; index < count && index < 8; index++)
            {
                try
                {
                    var entity = mate.MateEntity(index) as MateEntity2;
                    if (entity == null)
                    {
                        endpoints.Add(index + ":<无法读取端点>");
                        continue;
                    }

                    var component = entity.ReferenceComponent;
                    var componentName = component == null
                        ? "<总装或无组件>"
                        : SafeRead(() => component.Name2, "<未命名组件>");
                    var componentSuppression = component == null
                        ? "n/a"
                        : SafeRead(
                            () => component.GetSuppression().ToString(CultureInfo.InvariantCulture),
                            "unknown");
                    var referenceName = SafeRead(
                        () => (entity.Reference as IFeature)?.Name,
                        "<无特征名>");
                    var referenceType = SafeRead(
                        () => entity.ReferenceType2.ToString(CultureInfo.InvariantCulture),
                        "unknown");
                    endpoints.Add(
                        index + ":"
                        + componentName + "::" + referenceName
                        + " referenceType2=" + referenceType
                        + " componentSuppression=" + componentSuppression);
                }
                catch (Exception exception)
                {
                    endpoints.Add(
                        index + ":端点读取失败="
                        + FormatDiagnosticException(exception));
                }
            }

            return endpoints;
        }

        private static int? TryReadArrayInt(object values, int index)
        {
            try
            {
                var array = values as Array;
                return array == null
                    ? (int?)null
                    : Convert.ToInt32(array.GetValue(index), CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static bool? TryReadArrayBool(object values, int index)
        {
            try
            {
                var array = values as Array;
                return array == null
                    ? (bool?)null
                    : Convert.ToBoolean(array.GetValue(index), CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static string SafeRead(Func<string> reader, string fallback)
        {
            try
            {
                var value = reader();
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }

        private static string FormatDiagnosticException(Exception exception)
        {
            return exception == null
                ? "<未知异常>"
                : exception.GetType().Name + ": " + exception.Message;
        }

        private void RestoreTotalAssemblyBaseline()
        {
            if (string.IsNullOrWhiteSpace(_baselineAssemblyPath)
                || !File.Exists(_baselineAssemblyPath))
            {
                throw new FileNotFoundException("总装配体基线备份不存在。", _baselineAssemblyPath);
            }

            var driver = new SolidWorksDriver(_swApp);
            driver.TryCloseDocumentByPath(_totalAssemblyPath);
            File.Copy(_baselineAssemblyPath, _totalAssemblyPath, overwrite: true);
            SetTotalAssembly(driver.OpenOrActivateDocument(_totalAssemblyPath));
        }

        private void CloseDocumentsUnderTransactionModelRoots()
        {
            foreach (var folderName in new[] { "基座", "绝缘子" })
            {
                new SolidWorksDriver(_swApp).CloseOpenDocumentsUnderDirectoryConfirmed(
                    Path.Combine(_projectPath, folderName),
                    new[] { _totalAssemblyPath });
            }
        }

        private void CloseTotalAssemblyForRollback()
        {
            // 先关闭并确认总装已消失，释放对子模型的引用，然后才能安全覆盖备份。
            new SolidWorksDriver(_swApp).CloseDocumentByPathConfirmed(_totalAssemblyPath);
        }

        private void TryRemoveRegisteredOutputs(ICollection<string> errors)
        {
            IReadOnlyCollection<string> references;
            try
            {
                references = GetReferencedBaseModelPaths();
            }
            catch (Exception exception)
            {
                errors.Add("读取回滚后引用失败，已保留新增文件：" + exception.Message);
                return;
            }

            foreach (var file in _createdFiles.OrderByDescending(path => path.Length))
            {
                try
                {
                    if (references.Contains(file, StringComparer.OrdinalIgnoreCase))
                    {
                        errors.Add("新增文件仍被引用，已保留：" + file);
                    }
                    else if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception exception)
                {
                    errors.Add("删除新增文件失败：" + file + "；" + exception.Message);
                }
            }

            foreach (var directory in _newDirectories.OrderByDescending(path => path.Length))
            {
                try
                {
                    if (references.Any(path => IsPathUnderDirectory(path, directory)))
                    {
                        errors.Add("新增目录仍被引用，已保留：" + directory);
                    }
                    else if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, true);
                    }
                }
                catch (Exception exception)
                {
                    errors.Add("删除新增目录失败：" + directory + "；" + exception.Message);
                }
            }
        }

        private static bool IsVirtualDocument(string path, Array statuses, int index)
        {
            if (!string.IsNullOrWhiteSpace(path) && path.IndexOf('^') >= 0)
            {
                return true;
            }

            if (statuses == null || index < 0 || index >= statuses.Length)
            {
                return false;
            }

            try
            {
                return Convert.ToInt32(statuses.GetValue(index), CultureInfo.InvariantCulture)
                    == (int)swPackAndGoDocumentStatus_e.swPackAndGoDocumentStatus_Virtual;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPathUnderDirectory(string path, string directory)
        {
            var prefix = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                var relative = directory.Substring(source.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(source.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, overwrite: true);
            }
        }

        private sealed class HostedInsulatorState
        {
            internal HostedInsulatorState(string modelPath, IModelDoc2 model)
            {
                ModelPath = modelPath;
                Model = model;
            }

            internal string ModelPath { get; }

            internal IModelDoc2 Model { get; }
        }

        private int PositionPriority(string positionName)
        {
            for (var index = 0; index < _orderedPositions.Count; index++)
            {
                if (string.Equals(
                    _orderedPositions[index],
                    positionName,
                    StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return int.MaxValue;
        }
    }
}
