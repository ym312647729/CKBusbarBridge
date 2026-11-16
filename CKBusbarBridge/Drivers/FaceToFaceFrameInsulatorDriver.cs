using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal sealed class FaceToFaceFrameInsulatorPlacementRoute
    {
        internal FaceToFaceFrameInsulatorPlacementRoute(
            string dimensionName,
            string frameCoordinateName,
            string insulatorCoordinateName)
        {
            DimensionName = dimensionName;
            FrameCoordinateName = frameCoordinateName;
            InsulatorCoordinateName = insulatorCoordinateName;
        }

        internal string DimensionName { get; }

        internal string FrameCoordinateName { get; }

        internal string InsulatorCoordinateName { get; }
    }

    internal interface IFaceToFaceFrameInsulatorAssemblyOperations
    {
        void SetPlacementDimension(string dimensionName, double position);

        object InsertInsulator(int insulatorIndex);

        void MateInsulator(
            int insulatorIndex,
            object insertedInsulator,
            FaceToFaceFrameInsulatorPlacementRoute route);

        void DeleteInsulator(int insulatorIndex, object insertedInsulator);

        bool SaveInsulator();

        FaceToFaceFrameCleanupConfirmation CloseInsulator();

        void RebuildFrame();
    }

    internal interface IFaceToFaceFrameIndexedInsulatorOperations
    {
        string TargetPath(int index);
    }

    /// <summary>
    /// 把公共绝缘子模型逐个装入一个面对面基架。绝缘子先保存关闭，
    /// 再重建基架；基架本身留给总装装配器在最终关闭前保存。
    /// </summary>
    internal sealed partial class FaceToFaceFrameInsulatorDriver
        : IFaceToFaceFrameInsulatorDriver,
          IFaceToFaceFrameExistingInsulatorReconciler
    {
        private const string InsulatorCoordinateName = "安装坐标系";
        internal const string InstallationReferencePrefix = "母线桥绝缘子安装位";
        private readonly ISldWorks _swApp;
        private readonly IModelDoc2 _restoreContextModel;
        private readonly InsulatorDriver _insulatorDriver;
        private readonly InsulatorDriveRecoverySession _driveRecovery;
        private readonly bool _isolateBridgeOutputs;
        private readonly Func<string> _resolveTemplateRoot;

        internal FaceToFaceFrameInsulatorDriver(ISldWorks swApp, bool isolateBridgeOutputs) : this(swApp)
        { _isolateBridgeOutputs = isolateBridgeOutputs; }

        internal FaceToFaceFrameInsulatorDriver(ISldWorks swApp, bool isolateBridgeOutputs,
            InsulatorDriveRecoverySession driveRecovery, Func<string> resolveTemplateRoot)
            : this(swApp, driveRecovery, isolateBridgeOutputs)
        { _resolveTemplateRoot = resolveTemplateRoot; }

        internal FaceToFaceFrameInsulatorDriver(ISldWorks swApp, bool isolateBridgeOutputs, Func<string> resolveTemplateRoot)
            : this(swApp, isolateBridgeOutputs)
        { _resolveTemplateRoot = resolveTemplateRoot ?? throw new ArgumentNullException(nameof(resolveTemplateRoot)); }

        private string TemplateRoot() => _resolveTemplateRoot == null ? TemplateArchiveCacheManager.GetTemplateRootPath() : _resolveTemplateRoot();

        /// <summary>
        /// 首次生成基架时总装配体在物理模型准备期间保持关闭，
        /// 因此不恢复到总装编辑上下文。
        /// </summary>
        internal FaceToFaceFrameInsulatorDriver(ISldWorks swApp)
            : this(swApp, (InsulatorDriveRecoverySession)null, false) { }

        private FaceToFaceFrameInsulatorDriver(ISldWorks swApp, InsulatorDriveRecoverySession driveRecovery, bool isolateBridgeOutputs)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _insulatorDriver = new InsulatorDriver(swApp, driveRecovery);
            _driveRecovery = driveRecovery;
            _isolateBridgeOutputs = isolateBridgeOutputs;
        }

        internal FaceToFaceFrameInsulatorDriver(
            ISldWorks swApp,
            IModelDoc2 restoreContextModel,
            InsulatorDriveRecoverySession driveRecovery = null)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _restoreContextModel = restoreContextModel
                ?? throw new ArgumentNullException(nameof(restoreContextModel));
            _insulatorDriver = new InsulatorDriver(swApp, driveRecovery);
            _driveRecovery = driveRecovery;
        }

        public string PrepareInsulator(
            string projectPath,
            FaceToFaceFrameInsulatorDrivePlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            if (plan.Mounts != null) return PrepareMountedInsulators(projectPath, plan);

            if (plan.UseOffsetTemplate) return PrepareOffsetInsulators(projectPath, plan);

            var templateRoot = TemplateRoot();
            var templateAssemblyPath = plan.UseLBracketTemplate
                ? InsulatorDriver.ResolveLBracketInsulatorAssemblyPath(templateRoot)
                : InsulatorDriver.ResolveConventionalInsulatorAssemblyPath(templateRoot);
            var templateDirectory = Path.GetDirectoryName(templateAssemblyPath);
            var path = _insulatorDriver.PrepareUniversalThreePadInsulatorFromTemplateDirectory(
                plan.UseLBracketTemplate,
                projectPath,
                templateDirectory,
                plan.BusbarSpec,
                plan.PhaseSpacing,
                plan.InsulatorHeight,
                plan.BracketHostSpan,
                plan.SinglePhaseRootCount,
                _restoreContextModel,
                _isolateBridgeOutputs ? Path.Combine("母线桥", plan.CacheKey) : plan.CacheKey,
                templateAssemblyFileName: Path.GetFileName(templateAssemblyPath),
                modelFileNameSuffix: _isolateBridgeOutputs ? "母线桥" : null);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException(
                    "面对面基架绝缘子驱动完成后未找到可装配模型：" + path,
                    path);
            }

            return Path.GetFullPath(path);
        }

        private readonly Dictionary<string, string[]> _preparedOffsetSets =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        private string PrepareOffsetInsulators(string projectPath, FaceToFaceFrameInsulatorDrivePlan plan)
        {
            var template = InsulatorDriver.ResolveConventionalInsulatorAssemblyPath(TemplateRoot());
            if (!File.Exists(template)) throw new FileNotFoundException("未找到可偏移绝缘子模板：" + template, template);
            var paths = new List<string>();
            string identity;
            using (var hash = System.Security.Cryptography.SHA256.Create())
                identity = BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(plan.CacheKey)))
                    .Replace("-", string.Empty).Substring(0, 12);
            var layoutDirectory = Path.Combine(projectPath, "绝缘子", _isolateBridgeOutputs ? "母线桥" : string.Empty, plan.CacheKey);
            try
            {
                // 同一基架可有多个安装位，各位横移不同；每种横移必须使用独立文件与完整子零件身份。
                foreach (var offset in plan.TransverseOffsets)
                {
                    var suffix = "P" + offset.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    var directory = Path.Combine(_isolateBridgeOutputs ? "母线桥" : string.Empty, plan.CacheKey, suffix);
                    paths.Add(Path.GetFullPath(_insulatorDriver.PrepareOffsetThreePadInsulator(projectPath,
                        Path.GetDirectoryName(template), plan.BusbarSpec, plan.PhaseSpacing, plan.InsulatorHeight,
                        plan.BracketHostSpan, plan.SinglePhaseRootCount, _restoreContextModel, directory,
                        (_isolateBridgeOutputs ? "母线桥-" : string.Empty) + suffix + "-" + identity, offset)));
                }
                _preparedOffsetSets[paths[0]] = paths.ToArray();
                return paths[0];
            }
            catch (Exception primaryException)
            {
                // 资源事务边界：后续安装位失败时，也必须关闭前面已准备但尚未返回给宿主的副本。
                try
                {
                    var cleanup = new FaceToFaceFrameScopedDocumentCleaner(new SolidWorksFaceToFaceFrameDocumentSession(_swApp))
                        .RequestCloseDocumentsUnderDirectory(layoutDirectory);
                    FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(primaryException, cleanup, layoutDirectory, "可偏移绝缘子副本准备");
                }
                catch (Exception cleanupException)
                {
                    FaceToFaceFrameCleanupDiagnostics.AppendData(primaryException,
                        FaceToFaceFrameCleanupDiagnostics.RollbackWarningDataKey, "可偏移绝缘子副本关闭失败：" + cleanupException);
                }
                throw;
            }
        }

        private string[] PreparedPaths(string path)
            => path != null && _preparedOffsetSets.TryGetValue(path, out var paths) ? paths : new[] { path };

        internal static int InstallationIndex(Component2 frame, Component2 child, int count, bool requireAlignedAxes = false)
        {
            Func<Component2, string, double[]> origin = (component, name) =>
            {
                var definition = component.FeatureByName(name)?.GetDefinition() as CoordinateSystemFeatureData;
                var coordinate = definition?.Transform?.ArrayData as double[];
                var transform = component.GetTotalTransform(false)?.ArrayData as double[];
                if (coordinate == null || transform == null)
                    throw new InvalidOperationException("无法读取绝缘子实际安装坐标系：" + component.Name2 + "/" + name);
                var point = Enumerable.Range(0, 3).Select(i => transform[9 + i] +
                    Enumerable.Range(0, 3).Sum(j => coordinate[9 + j] * transform[3 * j + i]) * transform[12]);
                return !requireAlignedAxes ? point.ToArray() : point.Concat(Enumerable.Range(0, 9).Select(k =>
                    Enumerable.Range(0, 3).Sum(j => coordinate[3 * (k / 3) + j] * transform[3 * j + k % 3]))).ToArray();
            };
            var actual = origin(child, InsulatorCoordinateName);
            var hanging = IsHangingInsulator(child);
            var savedIndex = ReadInstallationIndex(child.ComponentReference, count, child.Name2);
            var candidates = savedIndex.HasValue ? new[] { savedIndex.Value } : Enumerable.Range(1, count);
            var matches = candidates.Where(i =>
            {
                var expected = origin(frame, ResolvePlacementRoute(i, hanging).FrameCoordinateName);
                return Enumerable.Range(0, actual.Length).All(axis => Math.Abs(actual[axis] - expected[axis]) <= .00001);
            }).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("绝缘子安装位无法唯一映射：" + child.Name2);
            return matches[0];
        }

        internal static int? ReadInstallationIndex(string reference, int count, string context)
        {
            if (reference == null || !reference.StartsWith(InstallationReferencePrefix, StringComparison.Ordinal)) return null;
            if (!int.TryParse(reference.Substring(InstallationReferencePrefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var index) || index < 1 || index > count)
                throw new InvalidOperationException("绝缘子安装位标识无效：组件=" + context + "；标识=" + reference + "；安装位数量=" + count);
            return index;
        }

        public void AssembleIntoFrame(
            string frameAssemblyPath,
            string insulatorAssemblyPath,
            FaceToFaceFrameModelPlan modelPlan)
        {
            if (modelPlan == null) throw new ArgumentNullException(nameof(modelPlan));
            var frameFullPath = RequireExistingPath(
                frameAssemblyPath,
                "待装配绝缘子的基架模型不存在");
            var insulatorFullPath = RequireExistingPath(
                insulatorAssemblyPath,
                "待装入基架的绝缘子模型不存在");

            var swDriver = new SolidWorksDriver(_swApp);
            var frameModel = _swApp.GetOpenDocumentByName(frameFullPath) as IModelDoc2
                ?? swDriver.OpenOrActivateDocument(frameFullPath);
            var insulatorModel = _swApp.GetOpenDocumentByName(insulatorFullPath) as IModelDoc2
                ?? swDriver.OpenOrActivateDocument(insulatorFullPath);
            ValidateOpenedAssembly(frameModel, frameFullPath, "基架");
            ValidateOpenedAssembly(insulatorModel, insulatorFullPath, "绝缘子");

            try
            {
                AssembleInsulators(
                    modelPlan,
                    new SolidWorksFrameInsulatorAssemblyOperations(
                        _swApp,
                        frameModel,
                        insulatorModel,
                        frameFullPath,
                        insulatorFullPath, insulatorPaths: PreparedPaths(insulatorFullPath)));
            }
            finally
            {
                // 总装装配器随后会重新激活基架并插入；先恢复总装上下文可避免 ActiveDoc 漂移。
                RestoreAssemblyEditingContextIfAvailable();
            }
        }

        public FaceToFaceFrameCleanupConfirmation CloseDocumentsAfterFailure(
            string frameAssemblyPath,
            string insulatorAssemblyPath)
        {
            var cleaner = new FaceToFaceFrameScopedDocumentCleaner(
                new SolidWorksFaceToFaceFrameDocumentSession(_swApp));
            var diagnostics = new List<string>();

            if (!string.IsNullOrWhiteSpace(insulatorAssemblyPath))
            {
                var insulatorClose = cleaner.RequestCloseDocumentsUnderDirectory(
                    _preparedOffsetSets.ContainsKey(insulatorAssemblyPath)
                        ? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(insulatorAssemblyPath))))
                        : Path.GetDirectoryName(Path.GetFullPath(insulatorAssemblyPath)));
                if (insulatorClose == null || !insulatorClose.IsConfirmed)
                {
                    diagnostics.Add(
                        "绝缘子文档关闭未确认："
                        + (insulatorClose?.Diagnostic ?? "未返回确认结果"));
                }
            }

            if (!string.IsNullOrWhiteSpace(frameAssemblyPath))
            {
                var frameClose = cleaner.CloseDocumentsUnderDirectory(
                    Path.GetDirectoryName(Path.GetFullPath(frameAssemblyPath)));
                if (frameClose == null || !frameClose.IsConfirmed)
                {
                    diagnostics.Add(
                        "基架文档关闭未确认："
                        + (frameClose?.Diagnostic ?? "未返回确认结果"));
                }
            }

            return diagnostics.Count == 0
                ? FaceToFaceFrameCleanupConfirmation.Confirmed()
                : FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                    string.Join("；", diagnostics));
        }

        public void ReconcileExistingFrame(
            string projectPath,
            string frameAssemblyPath,
            FaceToFaceFrameModelPlan targetPlan,
            FaceToFaceFrameInsulatorParameters insulatorParameters)
        {
            if (targetPlan == null) throw new ArgumentNullException(nameof(targetPlan));
            var frameFullPath = RequireExistingPath(
                frameAssemblyPath,
                "待对账绝缘子的基架模型不存在");
            string targetInsulatorPath = null;
            if (targetPlan.InsulatorCount > 0)
            {
                if (insulatorParameters == null)
                {
                    throw new ArgumentNullException(nameof(insulatorParameters));
                }

                targetInsulatorPath = PrepareInsulator(
                    projectPath,
                    insulatorParameters.CreateDrivePlan(targetPlan));
            }

            var swDriver = new SolidWorksDriver(_swApp);
            var frameModel = _swApp.GetOpenDocumentByName(frameFullPath) as IModelDoc2
                ?? swDriver.OpenOrActivateDocument(frameFullPath);
            ValidateOpenedAssembly(frameModel, frameFullPath, "基架");
            IModelDoc2 insulatorModel = null;
            if (!string.IsNullOrWhiteSpace(targetInsulatorPath))
            {
                insulatorModel = _swApp.GetOpenDocumentByName(targetInsulatorPath) as IModelDoc2
                    ?? swDriver.OpenOrActivateDocument(targetInsulatorPath);
                ValidateOpenedAssembly(insulatorModel, targetInsulatorPath, "绝缘子");
            }

            try
            {
                ReconcileInsulators(
                    targetPlan,
                    new SolidWorksFrameInsulatorAssemblyOperations(
                        _swApp,
                        frameModel,
                        insulatorModel,
                        frameFullPath,
                        targetInsulatorPath,
                        projectPath, PreparedPaths(targetInsulatorPath)));
            }
            catch (Exception primaryException)
            {
                FaceToFaceFrameCleanupConfirmation cleanup;
                try
                {
                    cleanup = CloseDocumentsAfterFailure(
                        frameFullPath,
                        targetInsulatorPath);
                }
                catch (Exception cleanupException)
                {
                    cleanup = FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                        "绝缘子对账失败后的文档关闭异常：" + cleanupException.Message);
                }

                FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(
                    primaryException,
                    cleanup,
                    Path.GetDirectoryName(frameFullPath),
                    "基架绝缘子对账失败后的文档关闭");
                throw;
            }
            finally
            {
                RestoreAssemblyEditingContextIfAvailable();
            }

            var close = new FaceToFaceFrameScopedDocumentCleaner(
                new SolidWorksFaceToFaceFrameDocumentSession(_swApp))
                .CloseDocumentsUnderDirectory(Path.GetDirectoryName(frameFullPath));
            if (close == null || !close.IsConfirmed)
            {
                var exception = new InvalidOperationException(
                    "基架绝缘子对账完成后的基架文档关闭未得到确认。");
                FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(
                    exception,
                    close,
                    Path.GetDirectoryName(frameFullPath),
                    "基架绝缘子对账后的文档关闭");
                throw exception;
            }
        }

        private void RestoreAssemblyEditingContextIfAvailable()
        {
            if (_restoreContextModel != null)
            {
                SWHelper.EnsureAssemblyEditingContext(_swApp, _restoreContextModel);
            }
        }

        internal static FaceToFaceFrameInsulatorPlacementRoute ResolvePlacementRoute(
            int insulatorIndex)
            => ResolvePlacementRoute(insulatorIndex, false);

        internal static FaceToFaceFrameInsulatorPlacementRoute ResolvePlacementRoute(int insulatorIndex, bool hanging)
        {
            if (insulatorIndex < 1 || insulatorIndex > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(insulatorIndex),
                    insulatorIndex,
                    "一个基架最多支持 3 个绝缘子。");
            }

            return new FaceToFaceFrameInsulatorPlacementRoute(
                hanging ? $"D{insulatorIndex}@绝缘子吊装控制草图" : $"D{insulatorIndex}@草图3",
                hanging ? $"绝缘子吊装坐标系{insulatorIndex}" : $"绝缘子支架坐标系{insulatorIndex}",
                InsulatorCoordinateName);
        }

        internal static void AssembleInsulators(
            FaceToFaceFrameModelPlan modelPlan,
            IFaceToFaceFrameInsulatorAssemblyOperations operations)
        {
            if (modelPlan == null) throw new ArgumentNullException(nameof(modelPlan));
            if (operations == null) throw new ArgumentNullException(nameof(operations));
            if (modelPlan.InsulatorCount == 0)
            {
                return;
            }

            if (modelPlan.InsulatorCount < 0
                || modelPlan.InsulatorCount > 3
                || modelPlan.InsulatorPositions == null
                || modelPlan.InsulatorPositions.Count != modelPlan.InsulatorCount)
            {
                throw new ArgumentException(
                    $"基架“{modelPlan.SourceName}”的绝缘子数量与定位计划无效。",
                    nameof(modelPlan));
            }

            var inserted = new List<KeyValuePair<int, object>>();
            try
            {
                for (var index = 1; index <= modelPlan.InsulatorCount; index++)
                {
                    var route = ResolvePlacementRoute(index, modelPlan.InternalInsulators?.Mounts?[index - 1]?.IsFloorStanding == false);
                    operations.SetPlacementDimension(
                        route.DimensionName,
                        modelPlan.InsulatorPositions[index - 1]);
                    var component = operations.InsertInsulator(index);
                    if (component == null)
                    {
                        throw new InvalidOperationException(
                            $"向基架“{modelPlan.SourceName}”插入绝缘子{index}失败。");
                    }

                    inserted.Add(new KeyValuePair<int, object>(index, component));
                    operations.MateInsulator(index, component, route);
                }

                if (!operations.SaveInsulator())
                {
                    throw new InvalidOperationException(
                        $"保存基架“{modelPlan.SourceName}”使用的绝缘子模型失败。");
                }

                var closeResult = operations.CloseInsulator();
                if (closeResult == null || !closeResult.IsConfirmed)
                {
                    var exception = new InvalidOperationException(
                        $"关闭基架“{modelPlan.SourceName}”使用的绝缘子模型失败或未得到确认。");
                    FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(
                        exception,
                        closeResult,
                        null,
                        "基架内部绝缘子文档关闭");
                    throw exception;
                }

                // 绝缘子已完成保存关闭后再重建基架；此处不保存基架。
                operations.RebuildFrame();
            }
            catch (Exception primaryException)
            {
                foreach (var item in inserted.AsEnumerable().Reverse())
                {
                    try
                    {
                        operations.DeleteInsulator(item.Key, item.Value);
                    }
                    catch (Exception cleanupException)
                    {
                        FaceToFaceFrameCleanupDiagnostics.AppendData(
                            primaryException,
                            FaceToFaceFrameCleanupDiagnostics.RollbackWarningDataKey,
                            $"删除基架内绝缘子{item.Key}失败：{cleanupException.Message}");
                    }
                }

                throw;
            }
        }

        internal static void ReconcileInsulators(
            FaceToFaceFrameModelPlan targetPlan,
            IFaceToFaceFrameExistingInsulatorOperations operations)
        {
            if (targetPlan == null) throw new ArgumentNullException(nameof(targetPlan));
            if (operations == null) throw new ArgumentNullException(nameof(operations));
            if (targetPlan.InsulatorCount < 0
                || targetPlan.InsulatorCount > 3
                || targetPlan.InsulatorPositions == null
                || targetPlan.InsulatorPositions.Count != targetPlan.InsulatorCount)
            {
                throw new ArgumentException(
                    $"基架“{targetPlan.SourceName}”的绝缘子数量与定位计划无效。",
                    nameof(targetPlan));
            }

            if (targetPlan.InsulatorCount > 0
                && string.IsNullOrWhiteSpace(operations.TargetInsulatorPath))
            {
                throw new ArgumentException("目标绝缘子模型路径不能为空。", nameof(operations));
            }

            var existingByIndex = new Dictionary<int, FaceToFaceFrameExistingInsulator>();
            foreach (var existing in operations.GetGeneratedInsulators()
                ?? Array.Empty<FaceToFaceFrameExistingInsulator>())
            {
                if (existing == null
                    || existing.Index < 1
                    || existing.Index > 3
                    || existing.NativeComponent == null
                    || string.IsNullOrWhiteSpace(existing.ModelPath)
                    || existingByIndex.ContainsKey(existing.Index))
                {
                    throw new ArgumentException("基架内已生成绝缘子的序号、路径或组件无效。", nameof(operations));
                }

                existingByIndex.Add(existing.Index, existing);
            }

            // 先删除超出目标数量的尾部组件，保留仍在使用的相同序号组件。
            foreach (var existing in existingByIndex.Values
                .Where(item => item.Index > targetPlan.InsulatorCount)
                .OrderByDescending(item => item.Index)
                .ToList())
            {
                operations.DeleteInsulator(existing.Index, existing.NativeComponent);
                existingByIndex.Remove(existing.Index);
            }

            var inserted = new List<KeyValuePair<int, object>>();
            try
            {
                for (var index = 1; index <= targetPlan.InsulatorCount; index++)
                {
                    var route = ResolvePlacementRoute(index, targetPlan.InternalInsulators?.Mounts?[index - 1]?.IsFloorStanding == false);
                    operations.SetPlacementDimension(
                        route.DimensionName,
                        targetPlan.InsulatorPositions[index - 1]);

                    if (existingByIndex.TryGetValue(index, out var existing))
                    {
                        var indexed = operations as IFaceToFaceFrameIndexedInsulatorOperations;
                        if (IsSamePath(existing.ModelPath, indexed == null ? operations.TargetInsulatorPath : indexed.TargetPath(index)))
                        {
                            continue;
                        }

                        operations.DeleteInsulator(index, existing.NativeComponent);
                    }

                    var component = operations.InsertInsulator(index);
                    if (component == null)
                    {
                        throw new InvalidOperationException(
                            $"向基架“{targetPlan.SourceName}”插入绝缘子{index}失败。");
                    }

                    inserted.Add(new KeyValuePair<int, object>(index, component));
                    operations.MateInsulator(index, component, route);
                }

                if (targetPlan.InsulatorCount > 0)
                {
                    if (!operations.SaveInsulator())
                    {
                        throw new InvalidOperationException(
                            $"保存基架“{targetPlan.SourceName}”使用的绝缘子模型失败。");
                    }

                    var closeResult = operations.CloseInsulator();
                    if (closeResult == null || !closeResult.IsConfirmed)
                    {
                        var exception = new InvalidOperationException(
                            $"关闭基架“{targetPlan.SourceName}”使用的绝缘子模型失败或未得到确认。");
                        FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(
                            exception,
                            closeResult,
                            null,
                            "基架二次驱动的绝缘子文档关闭");
                        throw exception;
                    }
                }

                operations.RebuildAndSaveFrame();
            }
            catch (Exception primaryException)
            {
                foreach (var item in inserted.AsEnumerable().Reverse())
                {
                    try
                    {
                        operations.DeleteInsulator(item.Key, item.Value);
                    }
                    catch (Exception cleanupException)
                    {
                        FaceToFaceFrameCleanupDiagnostics.AppendData(
                            primaryException,
                            FaceToFaceFrameCleanupDiagnostics.RollbackWarningDataKey,
                            $"删除二次驱动新插入的绝缘子{item.Key}失败：{cleanupException.Message}");
                    }
                }

                throw;
            }
        }

        private static bool IsSamePath(string first, string second)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(first),
                    Path.GetFullPath(second),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string RequireExistingPath(string path, string message)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException(message + "：" + path, path);
            }

            return Path.GetFullPath(path);
        }

        private static void ValidateOpenedAssembly(
            IModelDoc2 model,
            string requestedPath,
            string modelLabel)
        {
            var actualPath = model?.GetPathName();
            if (model == null
                || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY
                || !IsOpenedDocumentPathMatch(actualPath, requestedPath))
            {
                throw new InvalidOperationException(
                    $"打开的{modelLabel}模型路径与请求不一致：请求={requestedPath}；实际={actualPath}");
            }
        }

        internal static bool IsOpenedDocumentPathMatch(
            string actualPath,
            string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(actualPath)
                || string.IsNullOrWhiteSpace(requestedPath))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(actualPath),
                    Path.GetFullPath(requestedPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private sealed class SolidWorksFrameInsulatorAssemblyOperations
            : IFaceToFaceFrameInsulatorAssemblyOperations,
              IFaceToFaceFrameExistingInsulatorOperations, IFaceToFaceFrameIndexedInsulatorOperations
        {
            private readonly ISldWorks _swApp;
            private readonly IModelDoc2 _frameModel;
            private readonly AssemblyDoc _frameAssembly;
            private readonly IModelDoc2 _insulatorModel;
            private readonly string _framePath;
            private readonly string _insulatorPath;
            private readonly string _projectPath;
            private readonly string[] _insulatorPaths;
            private readonly bool _closePositionTree;

            internal SolidWorksFrameInsulatorAssemblyOperations(
                ISldWorks swApp,
                IModelDoc2 frameModel,
                IModelDoc2 insulatorModel,
                string framePath,
                string insulatorPath,
                string projectPath = null, string[] insulatorPaths = null)
            {
                _swApp = swApp;
                _frameModel = frameModel;
                _frameAssembly = frameModel as AssemblyDoc
                    ?? throw new InvalidOperationException("基架文档不是有效装配体：" + framePath);
                _insulatorModel = insulatorModel;
                _framePath = framePath;
                _insulatorPath = insulatorPath;
                _projectPath = projectPath;
                _insulatorPaths = insulatorPaths ?? new[] { insulatorPath };
                _closePositionTree = _insulatorPaths.Length > 1 || !string.IsNullOrEmpty(_insulatorPath) && _insulatorPath.IndexOf("-P-", StringComparison.Ordinal) >= 0 ||
                    insulatorModel != null && !string.IsNullOrWhiteSpace(new ReferencedModelValueReader(insulatorModel).TryProperty(HangingPlanProperty));
            }

            public IReadOnlyList<FaceToFaceFrameExistingInsulator> GetGeneratedInsulators()
            {
                if (string.IsNullOrWhiteSpace(_projectPath))
                {
                    return Array.Empty<FaceToFaceFrameExistingInsulator>();
                }

                var insulatorRoot = Path.Combine(Path.GetFullPath(_projectPath), "绝缘子");
                var components = ((_frameAssembly.GetComponents(true) as object[])
                        ?? Array.Empty<object>())
                    .OfType<Component2>()
                    .Where(component => !component.IsSuppressed()
                        && FaceToFaceFramePathClassifier.IsPathUnderDirectory(
                            insulatorRoot,
                            component.GetPathName()))
                    .OrderBy(component => component.Name2, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return components
                    .Select((component, index) => new FaceToFaceFrameExistingInsulator(
                        InstallationIndexInDocument(component),
                        component.GetPathName(),
                        component))
                    .ToArray();
            }

            private int InstallationIndexInDocument(Component2 child)
            {
                // 基架是当前文档根，子组件安装坐标系与根坐标系直接比较，不能依赖COM枚举或文件名顺序。
                var definition = child.FeatureByName(InsulatorCoordinateName)?.GetDefinition() as CoordinateSystemFeatureData;
                var local = definition?.Transform?.ArrayData as double[];
                var transform = child.Transform2?.ArrayData as double[];
                if (local == null || transform == null) throw new InvalidOperationException("绝缘子缺少安装坐标系：" + child.Name2);
                var actual = Enumerable.Range(0, 3).Select(i => transform[9 + i] +
                    Enumerable.Range(0, 3).Sum(j => local[9 + j] * transform[3 * j + i]) * transform[12]).ToArray();
                var savedIndex = ReadInstallationIndex(child.ComponentReference, 3, child.Name2);
                var candidates = savedIndex.HasValue ? new[] { savedIndex.Value } : Enumerable.Range(1, 3);
                var matches = candidates.Where(index =>
                {
                    var data = ((_frameAssembly.FeatureByName(ResolvePlacementRoute(index, IsHangingInsulator(child)).FrameCoordinateName) as Feature)?.GetDefinition()
                        as CoordinateSystemFeatureData)?.Transform?.ArrayData as double[];
                    return data != null && Enumerable.Range(0, 3).All(i => Math.Abs(data[9 + i] - actual[i]) <= .00001);
                }).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("基架内绝缘子安装位无法唯一映射：" + child.Name2);
                return matches[0];
            }

            public string TargetInsulatorPath => _insulatorPath;
            public string TargetPath(int index) => _insulatorPaths.Length == 1 ? _insulatorPath : _insulatorPaths[index - 1];

            public void SetPlacementDimension(string dimensionName, double position)
            {
                SWHelper.EnsureAssemblyEditingContext(_swApp, _frameModel);
                var dimension = _frameModel.Parameter(dimensionName) as Dimension;
                if (dimension == null)
                {
                    throw new InvalidOperationException(
                        $"基架缺少绝缘子定位尺寸“{dimensionName}”：{_framePath}");
                }

                dimension.SystemValue = position / 1000d;
                EnsureFrameRebuild("绝缘子定位尺寸驱动", dimensionName);
            }

            public object InsertInsulator(int insulatorIndex)
            {
                var path = TargetPath(insulatorIndex);
                var model = _swApp.GetOpenDocumentByName(path) as IModelDoc2
                    ?? new SolidWorksDriver(_swApp).OpenOrActivateDocument(path);
                ValidateOpenedAssembly(model, path, "绝缘子");
                SWHelper.EnsureAssemblyEditingContext(_swApp, _frameModel);
                var component = _frameAssembly.AddComponent5(
                    path,
                    (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                    string.Empty,
                    false,
                    string.Empty,
                    0,
                    0,
                    0);
                if (component == null)
                {
                    throw new InvalidOperationException(
                        $"向基架插入绝缘子{insulatorIndex}失败：基架={_framePath}；绝缘子={_insulatorPath}");
                }

                return component;
            }

            public void MateInsulator(
                int insulatorIndex,
                object insertedInsulator,
                FaceToFaceFrameInsulatorPlacementRoute route)
            {
                var component = insertedInsulator as Component2
                    ?? throw new InvalidOperationException(
                        $"绝缘子{insulatorIndex}缺少有效的 SolidWorks 组件。");
                SWHelper.EnsureAssemblyEditingContext(_swApp, _frameModel);
                if (route.FrameCoordinateName.StartsWith("绝缘子吊装坐标系", StringComparison.Ordinal))
                {
                    var data = ((_frameAssembly.FeatureByName(route.FrameCoordinateName) as Feature)?.GetDefinition() as CoordinateSystemFeatureData)
                        ?.Transform?.ArrayData as double[];
                    var height = new ReferencedModelValueReader(_frameModel).Global("基架高度");
                    if (data == null || Math.Abs(data[0] + 1) > .000001 || Math.Abs(data[4] + 1) > .000001 ||
                        Math.Abs(data[8] - 1) > .000001 || Math.Abs(data[10] * 1000 - height +
                            CKBusbarBridge.Prototype.BridgePhaseChangeSupportPlanner.CeilingInsetMm) > .01)
                        throw new InvalidOperationException("普通基架吊装坐标系姿态或顶面基准不符合模板契约：" + _framePath);
                }
                if (!PublicMethod.AddMateUseCoord(
                    _swApp,
                    _frameAssembly,
                    null,
                    component,
                    route.FrameCoordinateName,
                    route.InsulatorCoordinateName))
                {
                    throw new InvalidOperationException(
                        $"基架内绝缘子{insulatorIndex}坐标系配合失败：基架={_framePath}；"
                        + $"绝缘子={_insulatorPath}；父级坐标系={route.FrameCoordinateName}；"
                        + $"子级坐标系={route.InsulatorCoordinateName}。" );
                }
                // 混合安装时，未使用的另一套坐标系可能与当前安装位重合。
                // 保存原装配索引供反读唯一定位，读取时仍须核验实际坐标、尺寸和姿态。
                component.ComponentReference = InstallationReferencePrefix + insulatorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (ReadInstallationIndex(component.ComponentReference, 3, component.Name2) != insulatorIndex)
                    throw new InvalidOperationException("绝缘子安装位标识写入失败：" + component.Name2);
            }

            public void DeleteInsulator(int insulatorIndex, object insertedInsulator)
            {
                var component = insertedInsulator as Component2
                    ?? throw new InvalidOperationException(
                        $"绝缘子{insulatorIndex}缺少可删除的 SolidWorks 组件。");
                var componentName = component.Name2;
                _frameModel.ClearSelection2(true);
                try
                {
                    var selectName = component.GetSelectByIDString();
                    var selected = !string.IsNullOrWhiteSpace(selectName)
                        && _frameModel.Extension.SelectByID2(
                            selectName,
                            "COMPONENT",
                            0,
                            0,
                            0,
                            false,
                            0,
                            null,
                            0);
                    if (!selected)
                    {
                        selected = component.Select(false);
                    }

                    var deleted = selected && _frameAssembly.DeleteSelections(
                        (int)swDeleteSelectionOptions_e.swDelete_Children);
                    var stillExists = (_frameAssembly.GetComponents(true) as object[])
                        ?.OfType<Component2>()
                        .Any(item => string.Equals(
                            item.Name2,
                            componentName,
                            StringComparison.OrdinalIgnoreCase)) == true;
                    if (!deleted || stillExists)
                    {
                        throw new InvalidOperationException(
                            $"删除基架内绝缘子{insulatorIndex}失败：{componentName}");
                    }
                }
                finally
                {
                    _frameModel.ClearSelection2(true);
                }
            }

            public bool SaveInsulator()
            {
                foreach (var path in _insulatorPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var model = _swApp.GetOpenDocumentByName(path) as IModelDoc2;
                    if (model == null) return false;
                    var errors = 0; var warnings = 0;
                    if (!model.Save3((int)(swSaveAsOptions_e.swSaveAsOptions_Silent |
                        swSaveAsOptions_e.swSaveAsOptions_SaveReferenced), ref errors, ref warnings) || errors != 0) return false;
                }
                return true;
            }

            public FaceToFaceFrameCleanupConfirmation CloseInsulator()
            {
                return new FaceToFaceFrameScopedDocumentCleaner(
                    new SolidWorksFaceToFaceFrameDocumentSession(_swApp))
                    .RequestCloseDocumentsUnderDirectory(
                        _closePositionTree
                            ? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_insulatorPath))) : Path.GetDirectoryName(_insulatorPath));
            }

            public void RebuildFrame()
            {
                EnsureFrameRebuild("绝缘子全部装配完成后的基架重建", null);
            }

            public void RebuildAndSaveFrame()
            {
                EnsureFrameRebuild("绝缘子对账完成后的基架重建", null);
                var errors = 0;
                var warnings = 0;
                var saved = _frameModel.Save3(
                    (int)(swSaveAsOptions_e.swSaveAsOptions_Silent
                        | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                    ref errors,
                    ref warnings);
                if (!saved || errors != 0)
                {
                    throw new InvalidOperationException(
                        $"保存绝缘子对账后的基架失败：{_framePath}；错误码={errors}");
                }
            }

            private void EnsureFrameRebuild(string operation, string dimensionName)
            {
                if (!_frameModel.EditRebuild3())
                {
                    throw new InvalidOperationException(
                        $"{operation}失败：基架={_framePath}"
                        + (string.IsNullOrWhiteSpace(dimensionName)
                            ? string.Empty
                            : $"；尺寸={dimensionName}"));
                }
            }
        }
    }
}
