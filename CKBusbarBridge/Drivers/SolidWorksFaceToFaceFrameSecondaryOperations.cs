using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 面对面基架二次生成的 SolidWorks 与文件事务边界。
    /// </summary>
    internal sealed class SolidWorksFaceToFaceFrameSecondaryOperations
        : IFaceToFaceFrameSecondaryAssemblyOperations, IFaceToFaceFrameStoredModelReader
    {
        private readonly ISldWorks _swApp;
        private readonly string _totalAssemblyPath;
        private readonly IFrameBaseComponentResolver _baseComponentResolver;
        private readonly SolidWorksFaceToFaceFrameInitialGenerationSession _assemblySession;
        private IModelDoc2 _totalAssemblyModel;
        private AssemblyDoc _totalAssembly;
        private FaceToFaceFrameAssembler _assembler;
        private readonly IFaceToFaceFrameDocumentSession _documentSession;
        private readonly FaceToFaceFrameScopedDocumentCleaner _documentCleaner;
        private readonly IFaceToFaceBaseDirectoryCleaner _directoryCleaner;
        private readonly bool _allowLargeFrame;
        private readonly string _ordinaryBaseDisplayName;
        private readonly bool _allowDivergentLargeFrameWidth;
        private readonly FaceToFaceFrameSecondaryReadback _readback;
        private readonly Dictionary<string, string> _backupPathBySourceDirectory
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _registeredNewDirectories
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _recreatedDirectories
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _originalEmptyDirectories
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _projectPath;
        private string _frameRoot;
        private string _transactionRoot;
        private string _totalAssemblyBackupPath;

        internal SolidWorksFaceToFaceFrameSecondaryOperations(
            ISldWorks swApp,
            IModelDoc2 totalAssemblyModel)
            : this(
                swApp,
                totalAssemblyModel,
                allowLargeFrame: false,
                ordinaryBaseDisplayName: "B柜普通基座",
                allowDivergentLargeFrameWidth: false,
                baseComponentResolver: null,
                readback: null)
        {
        }

        /// <summary>
        /// 创建基架二次事务边界。面对墙通过读取选项启用大基架和独立大基架宽度，
        /// 其余装配、备份、回滚与提交语义继续复用面对面实现。
        /// </summary>
        internal SolidWorksFaceToFaceFrameSecondaryOperations(
            ISldWorks swApp,
            IModelDoc2 totalAssemblyModel,
            bool allowLargeFrame,
            string ordinaryBaseDisplayName,
            bool allowDivergentLargeFrameWidth)
            : this(
                swApp,
                totalAssemblyModel,
                allowLargeFrame,
                ordinaryBaseDisplayName,
                allowDivergentLargeFrameWidth,
                baseComponentResolver: null,
                readback: null)
        {
        }

        /// <summary>
        /// 注入当前场景的基座定位与真实读回策略。侧对侧通过该入口复用同一事务，
        /// 同时确保首段始终连接当前 Base1/Base2，而不是固定解析面对面普通基座。
        /// </summary>
        internal SolidWorksFaceToFaceFrameSecondaryOperations(
            ISldWorks swApp,
            IModelDoc2 totalAssemblyModel,
            bool allowLargeFrame,
            string ordinaryBaseDisplayName,
            bool allowDivergentLargeFrameWidth,
            IFrameBaseComponentResolver baseComponentResolver,
            FaceToFaceFrameSecondaryReadback readback)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            if (totalAssemblyModel == null)
            {
                throw new ArgumentNullException(nameof(totalAssemblyModel));
            }

            if (!(totalAssemblyModel is AssemblyDoc)
                || totalAssemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException(
                    "基架二次生成只能作用于已打开的 SolidWorks 总装配体。");
            }

            var totalAssemblyPath = totalAssemblyModel.GetPathName();
            if (string.IsNullOrWhiteSpace(totalAssemblyPath))
            {
                throw new InvalidOperationException(
                    "请先保存当前总装配体，再执行基架二次生成。");
            }

            _totalAssemblyPath = Path.GetFullPath(totalAssemblyPath);
            _baseComponentResolver = baseComponentResolver
                ?? new FaceToFaceFrameBaseComponentResolver();
            _assemblySession = new SolidWorksFaceToFaceFrameInitialGenerationSession(
                _swApp,
                totalAssemblyModel,
                _baseComponentResolver);
            BindTotalAssemblyContext(totalAssemblyModel);
            _documentSession = new SolidWorksFaceToFaceFrameDocumentSession(_swApp);
            _documentCleaner = new FaceToFaceFrameScopedDocumentCleaner(_documentSession);
            _directoryCleaner = new FaceToFaceBaseDirectoryCleaner();
            _allowLargeFrame = allowLargeFrame;
            _ordinaryBaseDisplayName = string.IsNullOrWhiteSpace(ordinaryBaseDisplayName)
                ? "普通基座"
                : ordinaryBaseDisplayName.Trim();
            _allowDivergentLargeFrameWidth = allowDivergentLargeFrameWidth;
            _readback = readback;
        }

        /// <summary>
        /// 当前重新绑定后的总装文档。调用方不得缓存构造函数传入的旧 COM 对象；
        /// 二次事务关闭并重开总装后，提交元数据和界面刷新都必须使用该实例。
        /// </summary>
        internal IModelDoc2 CurrentTotalAssemblyModel
        {
            get
            {
                return _totalAssemblyModel
                    ?? throw new InvalidOperationException(
                        "总装配体当前处于关闭状态，不能执行提交元数据操作。");
            }
        }

        public FaceToFaceFrameParameterSnapshot CaptureCurrentSnapshot(
            string projectPath,
            string ordinaryBasePositionName)
        {
            _projectPath = Path.GetFullPath(projectPath);
            _frameRoot = Path.Combine(_projectPath, "基架");
            EnsureNoUnfinishedTransaction();

            var readResult = ReadResult(
                _projectPath,
                ordinaryBasePositionName,
                expectedMappingJson: null);
            return FaceToFaceFrameParameterSnapshot.FromReadResult(readResult);
        }

        public void SaveBaseline()
        {
            SaveTotalAssembly("保存基架二次生成基线");
            EnsureTransactionRoot();

            // 后续会把“已预拆”的总装保存到原路径，因此仅靠 ReloadOrReplace 已无法回到完整基线。
            // 这里保存独立文件副本，回滚时在总装完全关闭后原路径覆盖恢复。
            _totalAssemblyBackupPath = Path.Combine(
                _transactionRoot,
                ".__total_assembly_baseline" + Path.GetExtension(_totalAssemblyPath));
            File.Copy(_totalAssemblyPath, _totalAssemblyBackupPath, true);

            // 标记只在总装副本完整写入后创建。进程异常留下该标记时，下次运行会阻止新事务，
            // 避免新的备份覆盖人工仍需恢复的旧事务现场。
            File.WriteAllText(
                Path.Combine(_transactionRoot, "transaction.state"),
                _totalAssemblyPath);
        }

        public string CaptureMapping()
        {
            return _assembler.CaptureModelMapping();
        }

        public void BackupModelDirectory(FaceToFaceFrameModelSnapshot model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            EnsureContextCaptured();
            var sourceDirectory = ValidateFrameChildDirectory(
                _projectPath,
                Path.GetDirectoryName(model.ModelPath));
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("待备份的基架目录不存在：" + sourceDirectory);
            }

            if (_backupPathBySourceDirectory.ContainsKey(sourceDirectory))
            {
                return;
            }

            EnsureTransactionRoot();
            var backupDirectory = ValidateTransactionChildDirectory(
                Path.Combine(_transactionRoot, model.SourceName));
            CopyDirectory(sourceDirectory, backupDirectory);
            _backupPathBySourceDirectory.Add(sourceDirectory, backupDirectory);
        }

        public void RegisterNewModelDirectory(string directoryPath)
        {
            EnsureContextCaptured();
            var safeDirectory = ValidateFrameChildDirectory(_projectPath, directoryPath);
            if (File.Exists(safeDirectory) || (Directory.Exists(safeDirectory)
                && Directory.EnumerateFileSystemEntries(safeDirectory).Any()))
            {
                throw new IOException("新增基架目标目录已存在，拒绝覆盖：" + safeDirectory);
            }

            if (Directory.Exists(safeDirectory)) _originalEmptyDirectories.Add(safeDirectory);
            _registeredNewDirectories.Add(safeDirectory);
        }

        public void RegisterRecreatedModelDirectory(
            FaceToFaceFrameModelSnapshot existingModel,
            string directoryPath)
        {
            if (existingModel == null) throw new ArgumentNullException(nameof(existingModel));
            EnsureContextCaptured();
            var safeDirectory = ValidateFrameChildDirectory(_projectPath, directoryPath);
            var existingDirectory = ValidateFrameChildDirectory(
                _projectPath,
                Path.GetDirectoryName(existingModel.ModelPath));
            if (!string.Equals(
                    safeDirectory,
                    existingDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"模板迁移基架“{existingModel.SourceName}”必须在原 SourceName 目录重建："
                    + $"原目录={existingDirectory}；目标目录={safeDirectory}");
            }

            if (!_backupPathBySourceDirectory.ContainsKey(existingDirectory))
            {
                throw new InvalidOperationException(
                    "删除模板迁移旧基架前尚未完成目录备份：" + existingDirectory);
            }

            var close = _documentCleaner.CloseDocumentsUnderDirectory(existingDirectory);
            if (close == null || !close.IsConfirmed)
            {
                throw new InvalidOperationException(
                    "删除模板迁移旧基架前无法确认文档已关闭："
                    + existingDirectory + "；" + (close?.Diagnostic ?? "未返回关闭确认"));
            }

            if (!_directoryCleaner.TryDelete(existingDirectory, out var deleteError))
            {
                throw new IOException(
                    "删除模板迁移旧基架目录失败：" + existingDirectory,
                    deleteError);
            }

            // 该路径随后由 PrepareNewModel 重新创建。既登记为新目录，又单独标记为重建目录，
            // 回滚时才能先清理新树再恢复旧备份，而不会把恢复后的目录当成普通新增目录再次删除。
            _registeredNewDirectories.Add(existingDirectory);
            _recreatedDirectories.Add(existingDirectory);
        }

        public FrameTemplateIdentity ReadStoredTemplateIdentity(string modelPath)
        {
            var fullPath = Path.GetFullPath(modelPath);
            ValidateFrameChildDirectory(_projectPath, Path.GetDirectoryName(fullPath));
            if (!FaceToFaceFramePathClassifier.TryGetStoredTemplateIdentity(fullPath, out var fileIdentity))
                throw new InvalidOperationException("无法识别已有基架主模型：" + fullPath);

            var before = new HashSet<string>(_documentSession.GetOpenDocuments()
                .Select(doc => doc.FilePath), StringComparer.OrdinalIgnoreCase);
            try
            {
                var model = _swApp.GetOpenDocumentByName(fullPath) as IModelDoc2;
                if (model == null)
                {
                    var errors = 0;
                    var warnings = 0;
                    model = _swApp.OpenDoc6(fullPath, (int)swDocumentTypes_e.swDocASSEMBLY,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent
                        | (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly,
                        string.Empty, ref errors, ref warnings) as IModelDoc2;
                    if (model == null || errors != 0)
                        throw new IOException($"读取已有基架模板失败：{fullPath}；错误码={errors}，警告码={warnings}");
                }

                if (!string.Equals(Path.GetFullPath(model.GetPathName()), fullPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SolidWorks 返回了不同路径的基架文档：" + fullPath);
                var value = new SolidWorksDriver(_swApp).GetCustomProperty(model, FrameTemplateIdentityMetadata.PropertyName);
                return FaceToFaceFramePathClassifier.ResolveStoredTemplateIdentity(fullPath, value);
            }
            finally
            {
                // 每次关闭后重取文档列表，避免父装配关闭后继续访问已经失效的 COM 包装。
                var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    var opened = _documentSession.GetOpenDocuments().FirstOrDefault(doc =>
                        !before.Contains(doc.FilePath) && !attempted.Contains(doc.FilePath)
                        && FaceToFaceFramePathClassifier.IsPathUnderDirectory(Path.GetDirectoryName(fullPath), doc.FilePath));
                    if (opened == null) break;
                    attempted.Add(opened.FilePath);
                    opened.Close();
                }
            }
        }

        public FaceToFaceFrameAssemblyToken InsertPosition(
            FaceToFaceFramePositionPlan targetPosition,
            string targetModelPath)
        {
            return _assembler.InsertAtPosition(targetPosition, targetModelPath);
        }

        public void RemovePosition(FaceToFaceFramePositionSnapshot oldPosition)
        {
            if (oldPosition == null) throw new ArgumentNullException(nameof(oldPosition));
            _assembler.DeleteComponentByInstanceId(oldPosition.ComponentInstanceId);
        }

        public void RebuildAfterDetach()
        {
            // 先在内存中刷新删除结果，确保保存关闭时装配引用树已经稳定。
            // 完整总装基线已由 SaveBaseline 复制到独立事务文件，稍后允许保存预拆状态。
            SolidWorksFaceToFaceFrameInitialGenerationSession.RebuildTopLevelAssembly(
                _totalAssemblyModel.ForceRebuild3, "预拆基架：" + _totalAssemblyPath);
        }

        public void SaveDetachedAssemblyAndClose()
        {
            EnsureTotalAssemblyBackupAvailable();
            _assemblySession.SaveAndCloseTotalAssembly();
            ClearBoundTotalAssemblyContext();
        }

        public void ReopenAssemblyAndForceRebuild()
        {
            // 基架及绝缘子在物理模型阶段已完成重建保存；重新打开后只刷新总装顶层，
            // 不递归进入未参与本次更新的铜排、柜体等子装配。
            var reopenedModel = _assemblySession.ReopenTotalAssemblyModelWithoutRebuild();
            SolidWorksFaceToFaceFrameInitialGenerationSession.RebuildTopLevelAssembly(
                reopenedModel.ForceRebuild3, _totalAssemblyPath);

            BindTotalAssemblyContext(reopenedModel);
        }

        public void RemateFinalChain(
            IReadOnlyList<FaceToFaceFrameAssemblyToken> finalTokens,
            string ordinaryBasePositionName)
        {
            if (finalTokens == null) throw new ArgumentNullException(nameof(finalTokens));

            if (PreserveValidChainMates)
            {
                FaceToFaceFrameAssemblyToken prior = null;
                foreach (var token in finalTokens.OrderBy(item => item.PositionPlan.PositionIndex))
                {
                    _assembler.RepairAdjacent(prior, token, ordinaryBasePositionName);
                    prior = token;
                }
                return;
            }

            // 位置替换会使原相邻配合两端失效；先只删除进/出坐标系链式配合，
            // 保留其他功能配合，再按最终位置顺序显式重建每一条相邻边。
            foreach (var token in finalTokens)
            {
                _assembler.DeleteFrameChainMates(token.ComponentFullName);
            }

            FaceToFaceFrameAssemblyToken previous = null;
            foreach (var token in finalTokens.OrderBy(item => item.PositionPlan.PositionIndex))
            {
                _assembler.MateAdjacent(previous, token, ordinaryBasePositionName);
                previous = token;
            }
        }

        internal bool PreserveValidChainMates { get; set; }
        internal FaceToFaceFrameSecondaryReadback FinalReadback { get; set; }

        public FaceToFaceFrameParameterSnapshot ValidateFinalChain(
            string projectPath,
            string ordinaryBasePositionName,
            string expectedMappingJson)
        {
            var readResult = ReadResult(
                projectPath,
                ordinaryBasePositionName,
                expectedMappingJson, targetTemplates: true);
            return FaceToFaceFrameParameterSnapshot.FromReadResult(readResult);
        }

        private FaceToFaceFrameReadResult ReadResult(
            string projectPath,
            string ordinaryBasePositionName,
            string expectedMappingJson, bool targetTemplates = false)
        {
            var readback = targetTemplates ? FinalReadback ?? _readback : _readback;
            return readback != null
                ? readback(
                    _totalAssemblyModel,
                    projectPath,
                    ordinaryBasePositionName,
                    expectedMappingJson)
                : CreateModelReadService().Read(
                    projectPath,
                    ordinaryBasePositionName);
        }

        private FaceToFaceFrameModelReadService CreateModelReadService()
        {
            return new FaceToFaceFrameModelReadService(
                new SolidWorksFaceToFaceFrameModelReadSource(
                    _totalAssemblyModel,
                    _allowLargeFrame,
                    _ordinaryBaseDisplayName,
                    expectedBaseComponentFullName: null,
                    ordinaryBaseAnchorCoordinateName: "安装坐标系",
                    baseAnchorRouteMode: FaceToFaceFrameBaseAnchorRouteMode.Either,
                    requireTopLevelSpatialBaseOwnership:
                        !_allowLargeFrame && _readback == null),
                _allowDivergentLargeFrameWidth,
                useSpatialFirstFrameSelection: _allowLargeFrame);
        }

        public void WriteMappingAndSave(string mappingJson)
        {
            if (string.IsNullOrWhiteSpace(mappingJson))
            {
                throw new ArgumentException("模型对照映射不能为空。", nameof(mappingJson));
            }

            var driver = new SolidWorksDriver(_swApp);
            driver.SetCustomProperty(
                _totalAssemblyModel,
                FaceToFaceBaseModelMappingHelper.PropertyName,
                mappingJson);
            var actual = driver.GetCustomProperty(
                _totalAssemblyModel,
                FaceToFaceBaseModelMappingHelper.PropertyName);
            if (!string.Equals(actual, mappingJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("写入基架最终模型对照映射失败。");
            }

            SolidWorksFaceToFaceFrameInitialGenerationSession.RebuildTopLevelAssembly(
                _totalAssemblyModel.ForceRebuild3, "写入基架最终映射：" + _totalAssemblyPath);

            SaveTotalAssembly("提交基架二次生成");
        }

        public void CloseTotalAssemblyWithoutSave()
        {
            _assemblySession.CloseTotalAssemblyWithoutSave();
            ClearBoundTotalAssemblyContext();
        }

        public IReadOnlyCollection<string> GetReferencedFrameModelPaths()
        {
            EnsureContextCaptured();
            var componentPaths = ((_totalAssembly.GetComponents(true) as object[]) ?? Array.Empty<object>())
                .OfType<Component2>()
                // 压缩组件仍持有磁盘引用，解除压缩后还会使用原模型，必须参与零引用判断。
                .Select(component => component.GetPathName())
                .ToArray();
            return FilterReferencedFrameModelPaths(_frameRoot, componentPaths);
        }

        internal static IReadOnlyCollection<string> FilterReferencedFrameModelPaths(
            string frameRoot,
            IEnumerable<string> topLevelComponentPaths)
        {
            if (topLevelComponentPaths == null)
            {
                throw new ArgumentNullException(nameof(topLevelComponentPaths));
            }

            var paths = topLevelComponentPaths.ToArray();
            if (paths.Any(string.IsNullOrWhiteSpace))
            {
                // 无法读取任一顶层组件路径时不能证明退休基架已零引用；调用方会保守保留目录。
                throw new InvalidOperationException(
                    "总装存在无法读取磁盘路径的顶层组件，无法安全确认退休基架目录为零引用。");
            }

            return paths
                .Where(path => FaceToFaceFramePathClassifier.IsPathUnderDirectory(frameRoot, path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public FaceToFaceFrameCleanupConfirmation CloseDocumentsUnderDirectory(
            string directoryPath)
        {
            return _documentCleaner.CloseDocumentsUnderDirectory(directoryPath);
        }

        public void RestoreBackupsAndRemoveNewDirectories()
        {
            EnsureContextCaptured();
            EnsureTotalAssemblyBackupAvailable();
            EnsureTotalAssemblyClosedForRestore();
            // 只处理服务在 PackAndGo 前显式登记的目标；不通过目录差集猜测，
            // 避免误删生成期间由用户或其他流程创建的同级目录。
            var newDirectories = _registeredNewDirectories
                .Where(Directory.Exists)
                .ToArray();
            var affectedDirectories = _backupPathBySourceDirectory.Keys
                .Concat(newDirectories)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var closeErrors = new List<string>();
            foreach (var directory in affectedDirectories)
            {
                var safeDirectory = ValidateFrameChildDirectory(_projectPath, directory);
                var close = _documentCleaner.CloseDocumentsUnderDirectory(safeDirectory);
                if (close == null || !close.IsConfirmed)
                {
                    closeErrors.Add(
                        safeDirectory + "：" + (close?.Diagnostic ?? "未返回关闭确认"));
                }
            }

            if (closeErrors.Count > 0)
            {
                throw new InvalidOperationException(
                    "恢复基架事务前无法确认相关文档已关闭：" + string.Join("；", closeErrors));
            }

            foreach (var pair in _backupPathBySourceDirectory)
            {
                var sourceDirectory = ValidateFrameChildDirectory(_projectPath, pair.Key);
                var backupDirectory = ValidateFrameChildDirectory(_projectPath, pair.Value);
                if (!_directoryCleaner.TryDelete(sourceDirectory, out var deleteError))
                {
                    throw new IOException(
                        "恢复备份前删除已修改基架目录失败：" + sourceDirectory,
                        deleteError);
                }

                CopyDirectory(backupDirectory, sourceDirectory);
            }

            foreach (var directory in newDirectories.Where(directory =>
                !_recreatedDirectories.Contains(directory)))
            {
                var safeDirectory = ValidateFrameChildDirectory(_projectPath, directory);
                if (!_directoryCleaner.TryDelete(safeDirectory, out var deleteError))
                {
                    throw new IOException("删除二次生成新建基架目录失败：" + safeDirectory, deleteError);
                }
                if (_originalEmptyDirectories.Contains(safeDirectory)) Directory.CreateDirectory(safeDirectory);
            }

            // 最后恢复总装文件。此时所有旧物理模型都已回到原路径，下一次打开不会出现断链；
            // 原路径上的“预拆状态”被完整基线副本覆盖，不再依赖 ReloadOrReplace 的内存语义。
            File.Copy(_totalAssemblyBackupPath, _totalAssemblyPath, true);
        }

        public void ClearTransactionBackups()
        {
            if (string.IsNullOrWhiteSpace(_transactionRoot) || !Directory.Exists(_transactionRoot))
            {
                return;
            }

            var safePath = ValidateFrameChildDirectory(_projectPath, _transactionRoot);
            if (!_directoryCleaner.TryDelete(safePath, out var cleanupError))
            {
                throw new IOException("删除基架二次生成事务备份失败：" + safePath, cleanupError);
            }

            _transactionRoot = null;
            _totalAssemblyBackupPath = null;
            _registeredNewDirectories.Clear();
            _recreatedDirectories.Clear();
            _originalEmptyDirectories.Clear();
            _backupPathBySourceDirectory.Clear();
        }

        internal static string ValidateFrameChildDirectory(
            string projectPath,
            string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)
                || string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new ArgumentException("项目路径和基架子目录路径不能为空。");
            }

            var frameRoot = Path.GetFullPath(Path.Combine(projectPath, "基架"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(directoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(frameRoot, candidate, StringComparison.OrdinalIgnoreCase)
                || !FaceToFaceFramePathClassifier.IsPathUnderDirectory(frameRoot, candidate))
            {
                throw new InvalidOperationException(
                    "拒绝操作基架根目录或其范围外的路径：" + candidate);
            }

            return candidate;
        }

        private void BindTotalAssemblyContext(IModelDoc2 model)
        {
            if (model == null
                || !(model is AssemblyDoc assembly)
                || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY
                || !PathsEqual(model.GetPathName(), _totalAssemblyPath))
            {
                throw new InvalidOperationException(
                    "无法把二次生成上下文绑定到原总装配体：" + _totalAssemblyPath);
            }

            _totalAssemblyModel = model;
            _totalAssembly = assembly;
            _assembler = new FaceToFaceFrameAssembler(
                _swApp,
                model,
                _baseComponentResolver);
        }

        private void ClearBoundTotalAssemblyContext()
        {
            // 关闭文档后旧 COM 对象即使仍可被托管字段访问，也不再代表有效 SolidWorks 文档。
            // 显式清空可把误用快速转化为确定异常，并强制 Reopen 后重新创建装配器。
            _assembler = null;
            _totalAssembly = null;
            _totalAssemblyModel = null;
        }

        internal void ReleaseCompletedModelContext()
        {
            // 事务备份继续由 Complete/Rollback 收尾，已完成装配不再持有旧文档句柄。
            ClearBoundTotalAssemblyContext();
            _assemblySession.ReleaseCompletedModelContext();
        }

        private void EnsureTotalAssemblyBackupAvailable()
        {
            if (string.IsNullOrWhiteSpace(_totalAssemblyBackupPath)
                || !File.Exists(_totalAssemblyBackupPath))
            {
                throw new InvalidOperationException(
                    "总装配体事务基线副本不存在，禁止保存预拆状态或执行文件恢复。");
            }
        }

        private void EnsureTotalAssemblyClosedForRestore()
        {
            var documents = _swApp.GetDocuments() as object[];
            var totalStillOpen = documents != null
                && documents
                    .OfType<IModelDoc2>()
                    .Any(document => PathsEqual(SafeGetPath(document), _totalAssemblyPath));
            if (totalStillOpen)
            {
                throw new InvalidOperationException(
                    "总装配体仍处于打开状态，禁止覆盖事务基线文件：" + _totalAssemblyPath);
            }
        }

        private void EnsureNoUnfinishedTransaction()
        {
            if (!Directory.Exists(_frameRoot))
            {
                return;
            }

            var unfinished = Directory.GetDirectories(
                    _frameRoot,
                    ".__secondary_transaction_*",
                    SearchOption.TopDirectoryOnly)
                .Where(directory => File.Exists(Path.Combine(directory, "transaction.state")))
                .ToArray();
            if (unfinished.Length > 0)
            {
                throw new InvalidOperationException(
                    "检测到未完成的基架二次生成文件事务，禁止开始新事务。请先人工核对并恢复："
                    + string.Join("；", unfinished));
            }
        }

        private void EnsureTransactionRoot()
        {
            if (!string.IsNullOrWhiteSpace(_transactionRoot))
            {
                return;
            }

            _transactionRoot = ValidateFrameChildDirectory(
                _projectPath,
                Path.Combine(
                    _frameRoot,
                    ".__secondary_transaction_" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_transactionRoot);
        }

        private string ValidateTransactionChildDirectory(string directoryPath)
        {
            var candidate = ValidateFrameChildDirectory(_projectPath, directoryPath);
            var transactionRoot = Path.GetFullPath(_transactionRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!FaceToFaceFramePathClassifier.IsPathUnderDirectory(
                    transactionRoot,
                    candidate))
            {
                throw new InvalidOperationException(
                    "事务备份目录必须位于本次事务根目录内：" + candidate);
            }

            return candidate;
        }

        private void EnsureContextCaptured()
        {
            if (string.IsNullOrWhiteSpace(_projectPath)
                || string.IsNullOrWhiteSpace(_frameRoot))
            {
                throw new InvalidOperationException("尚未捕获基架二次生成的项目上下文。");
            }
        }

        private void SaveTotalAssembly(string operation)
        {
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
                    $"{operation}失败：errors={errors}，warnings={warnings}。");
            }
        }

        private static void CopyDirectory(string sourceDirectory, string targetDirectory)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("复制源目录不存在：" + sourceDirectory);
            }

            Directory.CreateDirectory(targetDirectory);
            foreach (var file in Directory.GetFiles(sourceDirectory))
            {
                File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), true);
            }

            foreach (var child in Directory.GetDirectories(sourceDirectory))
            {
                CopyDirectory(child, Path.Combine(targetDirectory, Path.GetFileName(child)));
            }
        }

        private static string SafeGetPath(IModelDoc2 model)
        {
            try { return model?.GetPathName(); }
            catch { return null; }
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(left),
                    Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
