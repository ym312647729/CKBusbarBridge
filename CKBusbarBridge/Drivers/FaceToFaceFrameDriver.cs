using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 从基架模板创建已改名的完整引用树，并按需驱动为可复用源模型。
    /// </summary>
    internal sealed class FaceToFaceFrameDriver : IFaceToFaceFrameDriver
    {
        private readonly IFaceToFaceFrameModelOperations _operations;
        private readonly IDrawingNumberGenerator _drawingNumbers;

        internal FaceToFaceFrameDriver(IFaceToFaceFrameModelOperations operations, IDrawingNumberGenerator drawingNumbers = null)
        {
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            _drawingNumbers = drawingNumbers;
        }

        public string PrepareModel(
            FaceToFaceFrameModelPlan plan,
            string templateAssemblyPath,
            string outputDirectory,
            IDrawingNumberGenerator drawingNumbers)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (string.IsNullOrWhiteSpace(templateAssemblyPath)
                || !File.Exists(templateAssemblyPath))
            {
                throw new FileNotFoundException(
                    $"基架模板总装配体不存在：{templateAssemblyPath}",
                    templateAssemblyPath);
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("基架驱动输出目录不能为空。", nameof(outputDirectory));
            }

            if (drawingNumbers == null)
            {
                throw new ArgumentNullException(nameof(drawingNumbers));
            }

            // 即使尺寸等于模板值，也必须先让整棵引用树拥有唯一图号名称。
            // SolidWorks 以文档标题识别已打开模型，先驱动未改名模板会让后续位置
            // 打开同名、不同路径的文档，可能复用错误模型甚至触发原生崩溃。
            var drawingNumber = drawingNumbers.PeekNextDrawingNumber();
            var suffix = drawingNumbers.PeekNextFileNameSuffix();
            var templateDirectory = Path.GetDirectoryName(templateAssemblyPath);
            CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("准备基架模板开始", templateAssemblyPath);
            RunWithScopedDocumentCleanup(
                () => _operations.PackAndGoWithSuffix(
                    templateAssemblyPath,
                    outputDirectory,
                    suffix),
                templateDirectory,
                "基架模板 PackAndGo 后的源文档清理");
            CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("准备基架模板复制及清理完成", templateAssemblyPath);
            var outputAssemblyPath = Path.Combine(
                Path.GetFullPath(outputDirectory),
                Path.GetFileNameWithoutExtension(templateAssemblyPath) + suffix
                    + Path.GetExtension(templateAssemblyPath));
            if (!File.Exists(outputAssemblyPath))
            {
                throw new FileNotFoundException(
                    $"基架 PackAndGo 后未找到重命名总装配体：{outputAssemblyPath}",
                    outputAssemblyPath);
            }

            drawingNumbers.CommitNext(drawingNumber);

            // 有待写全局变量才驱动；常规基架模板初值字典为空，大基架始终写入自身变量名。
            if (plan.GlobalVariables != null && plan.GlobalVariables.Count > 0)
            {
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("基架参数驱动开始", outputAssemblyPath);
                RunWithScopedDocumentCleanupOnFailure(
                    () => _operations.ApplyGlobalVariablesAndRebuild(
                        outputAssemblyPath,
                        plan.GlobalVariables),
                    Path.GetFullPath(outputDirectory),
                    "基架参数驱动失败后的输出文档清理");
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("基架参数驱动完成", outputAssemblyPath);
            }

            if (plan.IsSmallOffsetFrame)
            {
                if (plan.SmallOffsetFrame == null)
                {
                    throw new InvalidOperationException(
                        $"小偏移基架“{plan.SourceName}”缺少孔偏移驱动参数。");
                }

                var sealPlatePlan = FaceToFaceSmallOffsetSealPlatePlanner.CreatePlan(
                    plan.SmallOffsetFrame,
                    plan.Height);
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("小偏移基架角钢封板驱动开始", outputAssemblyPath);
                RunWithScopedDocumentCleanupOnFailure(
                    () => _operations.ApplySmallOffsetSealPlatesAndRebuild(
                        outputAssemblyPath,
                        sealPlatePlan, drawingNumbers, plan.SuppressTemplateAngleNotches, plan.AngleNotches),
                    Path.GetFullPath(outputDirectory),
                    "小偏移基架角钢封板驱动失败后的输出文档清理");
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("小偏移基架角钢封板驱动完成", outputAssemblyPath);
            }

            RunWithScopedDocumentCleanupOnFailure(
                () =>
                {
                    // 物理基架返回装配流程前完成角钢处理，避免装配后切换活动总装。
                    if (plan.SuppressTemplateAngleNotches.HasValue)
                        _operations.ApplyTemplateAngleNotches(outputAssemblyPath, plan, drawingNumbers);
                    // 将稳定模板身份写进物理总装并保存，供反读和二次生成严格校验。
                    _operations.WriteCustomProperty(
                        outputAssemblyPath,
                        FrameTemplateIdentityMetadata.PropertyName,
                        FrameTemplateIdentityMetadata.Serialize(plan.TemplateIdentity));
                    WriteWallEntryDistance(outputAssemblyPath, plan);
                    _operations.SaveModel(outputAssemblyPath);
                },
                Path.GetFullPath(outputDirectory),
                "基架角钢及模板身份写入失败后的输出文档清理");

            return outputAssemblyPath;
        }

        public void DriveExistingModel(
            string assemblyPath,
            FaceToFaceFrameModelPlan targetPlan)
        {
            if (targetPlan == null)
            {
                throw new ArgumentNullException(nameof(targetPlan));
            }

            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new FileNotFoundException(
                    $"待二次驱动的基架模型不存在：{assemblyPath}",
                    assemblyPath);
            }

            var fullPath = Path.GetFullPath(assemblyPath);
            // 主体原位二次驱动不执行 PackAndGo；角钢参数分歧时使用共享图号源原生独立化。
            // 即使目标恢复模板默认值，三个尺寸仍必须全量写入。
            var variables = new Dictionary<string, object>(
                StringComparer.OrdinalIgnoreCase);
            if (targetPlan.IsSmallOffsetFrame || targetPlan.UseLargeFrameTemplate)
            {
                // 二次驱动小偏移及面对墙大基架时必须整体写入规划器变量；
                // 大基架2的“侧盖板宽度”并不包含在宽、长、高三个外形字段中。
                foreach (var pair in targetPlan.GlobalVariables)
                {
                    variables[pair.Key] = pair.Value;
                }
            }
            else
            {
                variables["基架宽度"] = targetPlan.Width;
                variables["基架长度"] = targetPlan.Length;
                variables["基架高度"] = targetPlan.Height;
            }
            // 原位二次驱动时基架仍被母线桥总装引用。这里只要求关闭本次打开的文档，
            // 允许其引用树继续驻留在 SolidWorks 中，不能把“仍加载”判定为失败。
            RunWithScopedDocumentCleanup(
                () =>
                {
                    _operations.ApplyGlobalVariablesAndRebuild(fullPath, variables);
                    if (targetPlan.IsSmallOffsetFrame)
                    {
                        if (targetPlan.SmallOffsetFrame == null)
                        {
                            throw new InvalidOperationException(
                                $"小偏移基架“{targetPlan.SourceName}”缺少孔偏移驱动参数。");
                        }

                        _operations.ApplySmallOffsetSealPlatesAndRebuild(
                            fullPath,
                            FaceToFaceSmallOffsetSealPlatePlanner.CreatePlan(
                                targetPlan.SmallOffsetFrame,
                                targetPlan.Height), _drawingNumbers, targetPlan.SuppressTemplateAngleNotches, targetPlan.AngleNotches);
                    }

                    if (targetPlan.SuppressTemplateAngleNotches.HasValue)
                        _operations.ApplyTemplateAngleNotches(fullPath, targetPlan, _drawingNumbers);
                    _operations.WriteCustomProperty(
                        fullPath,
                        FrameTemplateIdentityMetadata.PropertyName,
                        FrameTemplateIdentityMetadata.Serialize(
                            targetPlan.TemplateIdentity));
                    WriteWallEntryDistance(fullPath, targetPlan);
                    _operations.SaveModel(fullPath);
                },
                Path.GetDirectoryName(fullPath),
                "基架二次驱动后的文档关闭",
                TryConfirmDocumentCloseRequested);
        }

        private void WriteWallEntryDistance(string assemblyPath, FaceToFaceFrameModelPlan plan)
        {
            if (plan.FrameTemplateKind == FaceToWallFrameTemplateKind.Large1)
            {
                _operations.WriteCustomProperty(assemblyPath,
                    FaceToWallFrameDrivePlanner.WallEntryDistancePropertyName,
                    plan.WallEntryDistance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        private void RunWithScopedDocumentCleanup(
            Action operation,
            string sourceDirectory,
            string context)
        {
            RunWithScopedDocumentCleanup(
                operation,
                sourceDirectory,
                context,
                TryConfirmDocumentsClosed);
        }

        private void RunWithScopedDocumentCleanup(
            Action operation,
            string sourceDirectory,
            string context,
            Func<string, FaceToFaceFrameCleanupConfirmation> confirmCleanup)
        {
            try
            {
                operation();
            }
            catch (Exception operationException) when (!(operationException is FrameDriveInspectionRequiredException))
            {
                var cleanup = confirmCleanup(sourceDirectory);
                FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(
                    operationException,
                    cleanup,
                    sourceDirectory,
                    context);
                throw;
            }

            var confirmation = confirmCleanup(sourceDirectory);
            if (!confirmation.IsConfirmed)
            {
                var exception = new InvalidOperationException(
                    context + "未得到确认，已停止后续模型处理。" );
                FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(
                    exception,
                    confirmation,
                    sourceDirectory,
                    context);
                throw exception;
            }
        }

        private void RunWithScopedDocumentCleanupOnFailure(
            Action operation,
            string sourceDirectory,
            string context)
        {
            try
            {
                operation();
            }
            catch (Exception operationException) when (!(operationException is FrameDriveInspectionRequiredException))
            {
                var cleanup = TryConfirmDocumentsClosed(sourceDirectory);
                FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(
                    operationException,
                    cleanup,
                    sourceDirectory,
                    context);
                throw;
            }
        }

        private FaceToFaceFrameCleanupConfirmation TryConfirmDocumentsClosed(
            string sourceDirectory)
        {
            try
            {
                return _operations.CloseDocumentsUnderDirectory(sourceDirectory)
                    ?? FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                        "模型操作未返回文档清理确认。" );
            }
            catch (Exception cleanupException)
            {
                return FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                    "模型文档清理异常：" + cleanupException.Message);
            }
        }

        private FaceToFaceFrameCleanupConfirmation TryConfirmDocumentCloseRequested(
            string sourceDirectory)
        {
            try
            {
                return _operations.RequestCloseDocumentsUnderDirectory(sourceDirectory)
                    ?? FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                        "模型操作未返回文档关闭请求确认。");
            }
            catch (Exception cleanupException)
            {
                return FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                    "模型文档关闭请求异常：" + cleanupException.Message);
            }
        }
    }

    /// <summary>
    /// 基架驱动器所需 SolidWorks 操作的实现。
    /// </summary>
    internal sealed partial class SolidWorksFaceToFaceFrameModelOperations
        : IFaceToFaceFrameModelOperations
    {
        private readonly ISldWorks _swApp;

        internal SolidWorksFaceToFaceFrameModelOperations(ISldWorks swApp)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
        }

        public void ApplyGlobalVariablesAndRebuild(
            string assemblyPath,
            IReadOnlyDictionary<string, object> variables)
        {
            if (variables == null)
            {
                throw new ArgumentNullException(nameof(variables));
            }

            var variableMap = new Dictionary<string, object>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in variables)
            {
                variableMap[pair.Key] = pair.Value;
            }

            var driver = new SolidWorksDriver(_swApp);
            var model = driver.OpenOrActivateDocument(assemblyPath,
                rebuildAfterResolve: !variableMap.ContainsKey("进孔偏移尺寸"));
            UseDefaultModelConfiguration(model);
            if (variableMap.ContainsKey("进孔偏移尺寸")) DetachSmallOffsetSharedAngleEquations(model);
            ApplyGlobalVariablesAfterSmallOffsetCoverSelection(
                variableMap,
                useDoubleCover => ApplySmallOffsetCoverPlateFolders(model, useDoubleCover),
                () => driver.ApplyGlobalVariablesRequired(
                    model,
                    variableMap,
                    rebuildAfterChange: false));
            // 角钢由偏移执行器按实例驱动；此处不再通过主装变量联动001/003。
            // 后续ApplySmallOffsetSealPlatesAndRebuild完成几何更新及严格验收。
            if (variableMap.ContainsKey("进孔偏移尺寸")) return;
            if (!model.EditRebuild3())
            {
                throw new InvalidOperationException(
                    $"基架模型参数驱动后重建失败：{assemblyPath}");
            }

            SynchronizeFrameParentDimensions(model);

            // 面对墙接口差异在全部基架生成装配后统一校验，单架不拦截后续生成。

            // 驱动后还要在该基架内装配绝缘子，此处只重建并保持文档打开。
            // 基架最终装入总装后，现有装配器会在关闭前统一保存，避免重复 Save3。
        }

        internal static void ApplyGlobalVariablesAfterSmallOffsetCoverSelection(
            IReadOnlyDictionary<string, object> variables,
            Action<bool> selectSmallOffsetCovers,
            Action writeGlobalVariables)
        {
            if (variables.ContainsKey("进孔偏移尺寸"))
            {
                if (!variables.TryGetValue("基架宽度", out var widthValue) || widthValue == null)
                    throw new ArgumentException("小偏移基架盖板切换缺少基架宽度。", nameof(variables));
                var width = Convert.ToDouble(widthValue, System.Globalization.CultureInfo.InvariantCulture);
                if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0d)
                    throw new ArgumentOutOfRangeException(nameof(variables), "小偏移基架宽度必须为有效正数。");

                // 先完成盖板切换，避免按新宽度求解仍处于旧盖板结构的模型。
                selectSmallOffsetCovers(width > 1400d);
            }

            writeGlobalVariables();
        }

        private void ApplySmallOffsetCoverPlateFolders(IModelDoc2 model, bool useDoubleCover)
        {
            var path = model.GetPathName();
            if (!(model is IAssemblyDoc))
                throw new InvalidOperationException("小偏移基架盖板切换需要装配体模型：" + path);

            var folders = new[] { "单盖板", "双盖板" }.Select(name => new
            {
                Name = name,
                Feature = FindFeatureByName(model, name),
                Suppress = name == "单盖板" ? useDoubleCover : !useDoubleCover
            }).ToList();
            foreach (var folder in folders)
                if (!(folder.Feature?.GetSpecificFeature2() is FeatureFolder))
                    throw new InvalidOperationException(
                        $"小偏移基架盖板切换失败，未找到文件夹“{folder.Name}”；装配体={path}");

            SWHelper.EnsureAssemblyEditingContext(_swApp, model);
            // 两个文件夹均已确认存在，先压缩旧盖板，再解压目标盖板。
            foreach (var folder in folders.OrderByDescending(item => item.Suppress))
            {
                if (SealPlateFolderMatchesSuppression(model, folder.Feature, folder.Suppress, path))
                    continue;

                model.ClearSelection2(true);
                try
                {
                    if (!folder.Feature.Select2(false, -1))
                        throw new InvalidOperationException($"无法选择小偏移基架盖板文件夹“{folder.Name}”：{path}");
                    var succeeded = folder.Suppress ? model.EditSuppress2() : model.EditUnsuppress2();
                    if (!succeeded || !SealPlateFolderMatchesSuppression(model, folder.Feature, folder.Suppress, path))
                        throw new InvalidOperationException(
                            $"小偏移基架盖板文件夹“{folder.Name}”{(folder.Suppress ? "压缩" : "解压")}失败：{path}");
                }
                finally
                {
                    model.ClearSelection2(true);
                }
            }
        }

        public void ApplySmallOffsetSealPlatesAndRebuild(
            string assemblyPath,
            FaceToFaceSmallOffsetSealPlateDrivePlan plan,
            IDrawingNumberGenerator drawingNumbers = null, bool? suppressTemplateAngleNotches = null,
            CKBusbarBridge.Prototype.BridgeAngleNotchPlan angleNotches = null)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (plan.Folders == null || plan.Folders.Count != 4)
            {
                throw new ArgumentException(
                    "小偏移基架角钢封板计划必须包含四个文件夹。",
                    nameof(plan));
            }

            var driver = new SolidWorksDriver(_swApp);
            var rootModel = driver.OpenOrActivateDocument(assemblyPath, rebuildAfterResolve: false);
            UseDefaultModelConfiguration(rootModel);
            var rootAssembly = rootModel as IAssemblyDoc;
            if (rootAssembly == null)
            {
                throw new InvalidOperationException(
                    $"小偏移基架角钢封板驱动失败，目标不是装配体：{assemblyPath}");
            }

            var rootComponents = (rootAssembly.GetComponents(true) as object[])
                ?? Array.Empty<object>();
            const string smallOffsetBaseAssemblyPrefix = "6GX.355.032";
            var baseAssemblies = rootComponents
                .OfType<IComponent2>()
                .Where(component => ComponentIdentifierMatches(
                    component,
                    smallOffsetBaseAssemblyPrefix))
                .ToList();
            EnsureUniqueSmallOffsetSealPlateMatch(
                baseAssemblies.Count,
                smallOffsetBaseAssemblyPrefix + "*",
                assemblyPath);

            var baseAssembly = baseAssemblies[0];
            baseAssembly.SetSuppression2(
                (int)swComponentSuppressionState_e.swComponentResolved);
            EnsureSmallOffsetSealPlateComponentResolved(
                baseAssembly.IsSuppressed(),
                smallOffsetBaseAssemblyPrefix + "*",
                assemblyPath);
            var baseAssemblyModel = baseAssembly.GetModelDoc2() as IModelDoc2;
            if (baseAssemblyModel == null)
            {
                var baseAssemblyPath = baseAssembly.GetPathName();
                if (!string.IsNullOrWhiteSpace(baseAssemblyPath)
                    && File.Exists(baseAssemblyPath))
                {
                    baseAssemblyModel = driver.OpenOrActivateDocument(baseAssemblyPath, rebuildAfterResolve: false);
                }
            }

            if (baseAssemblyModel == null
                || baseAssemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException(
                    $"小偏移基架角钢封板驱动失败，无法打开 {smallOffsetBaseAssemblyPrefix}* 子装配体：{assemblyPath}");
            }

            var referencedConfiguration = RequireSmallOffsetReferencedConfiguration(
                baseAssembly.ReferencedConfiguration,
                NativeDefaultModelConfiguration(baseAssemblyModel),
                smallOffsetBaseAssemblyPrefix + "*",
                assemblyPath);
            baseAssemblyModel = driver.OpenOrActivateDocument(baseAssemblyModel.GetPathName(), rebuildAfterResolve: false);
            UseDefaultModelConfiguration(baseAssemblyModel);
            var originalConfiguration = GetRequiredActiveConfigurationName(
                baseAssemblyModel,
                smallOffsetBaseAssemblyPrefix + "*",
                assemblyPath);
            var switchedConfiguration = !string.Equals(
                referencedConfiguration,
                originalConfiguration,
                StringComparison.OrdinalIgnoreCase);
            Exception operationException = null;
            try
            {
                // 文件夹压缩命令作用于当前子装配体文档，先激活它再切换引用配置。
                baseAssemblyModel = driver.OpenOrActivateDocument(baseAssemblyModel.GetPathName(), rebuildAfterResolve: false);
                if (switchedConfiguration
                    && !baseAssemblyModel.ShowConfiguration2(referencedConfiguration))
                {
                    throw new InvalidOperationException(
                        $"小偏移基架角钢封板驱动失败，无法激活 {smallOffsetBaseAssemblyPrefix}* 引用配置"
                        + $"“{referencedConfiguration}”；装配体={assemblyPath}");
                }

                DriveSmallOffsetAngleFiles(rootModel, baseAssemblyModel, plan, drawingNumbers, suppressTemplateAngleNotches, angleNotches);
                SWHelper.EnsureAssemblyEditingContext(_swApp, rootModel);
                EnsureSmallOffsetSealPlateRebuildSucceeded(rootModel.EditRebuild3(), assemblyPath);
                ParentDimensionsForFrame(rootModel).Synchronize(rootModel);
                driver.OpenOrActivateDocument(baseAssemblyModel.GetPathName(), rebuildAfterResolve: false);
                ApplySealPlateFolderSuppression(baseAssemblyModel, plan, assemblyPath);
                EnsureSmallOffsetSealPlateRebuildSucceeded(
                    baseAssemblyModel.EditRebuild3(),
                    assemblyPath);

                // 压缩的子件可能不在原组件快照中，解压文件夹后必须重新读取。
                var baseAssemblyChildren = (((IAssemblyDoc)baseAssemblyModel).GetComponents(true) as object[])
                    ?.OfType<IComponent2>()
                    .ToList()
                    ?? new List<IComponent2>();
                var runtimeTargets = ResolveSealPlateFolderTargets(
                    baseAssemblyModel,
                    baseAssemblyChildren,
                    plan,
                    assemblyPath);

                foreach (var target in runtimeTargets)
                {
                    foreach (var component in target.Components)
                    {
                        var isPlate = ComponentIdentifierMatches(component, target.Plan.PlateComponentPrefix);
                        if (!isPlate && !ComponentIdentifierMatches(component, "8GX.160.167"))
                            throw new InvalidOperationException("小偏移封板文件夹包含未知组件：" + component.Name2);
                        var suppress = !isPlate && target.Plan.SuppressAngleSteel;
                        SetSmallOffsetComponentSuppression(component, suppress, assemblyPath);
                    }
                }

                foreach (var target in runtimeTargets.Where(
                    item => item.PlateComponent != null))
                {
                    DriveSmallOffsetSealPlateComponent(
                        driver,
                        rootModel,
                        target,
                        plan.FrameHeight,
                        assemblyPath);
                }

                EnsureSmallOffsetSealPlateRebuildSucceeded(
                    baseAssemblyModel.EditRebuild3(),
                    assemblyPath);
                EnsureSmallOffsetSealPlateRebuildSucceeded(
                    rootModel.EditRebuild3(),
                    assemblyPath);
                ValidateSmallOffsetSealComponents(baseAssemblyModel, plan, assemblyPath);
                EnsureSmallOffsetCoverPlateDimensions(driver, rootModel);
            }
            catch (Exception exception)
            {
                operationException = exception;
                throw;
            }
            finally
            {
                RestoreSmallOffsetConfigurationAndEditingContext(
                    operationException,
                    switchedConfiguration
                        ? (Func<bool>)(() => baseAssemblyModel.ShowConfiguration2(
                            originalConfiguration))
                        : null,
                    () => SWHelper.EnsureAssemblyEditingContext(_swApp, rootModel),
                    smallOffsetBaseAssemblyPrefix + "*",
                    originalConfiguration,
                    assemblyPath);
            }

            // 032 子装曾被单独激活为文档窗口，必须在主装仍打开时保存并关闭它。
            // SW2024 中先关闭主装再关闭该窗口，会使 163/165 零件驻留且无法重开。
            // CloseDoc 在主装仍引用它时只释放窗口；后续主装保存和清理再统一卸载模型。
            var baseSaveErrors = 0;
            var baseSaveWarnings = 0;
            EnsureSmallOffsetSealPlateSaveSucceeded(
                baseAssemblyModel.Save3(
                    (int)(swSaveAsOptions_e.swSaveAsOptions_Silent
                        | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                    ref baseSaveErrors,
                    ref baseSaveWarnings),
                baseSaveErrors,
                baseAssemblyModel.GetPathName());
            var baseTitle = baseAssemblyModel.GetTitle();
            if (string.IsNullOrWhiteSpace(baseTitle))
                throw new InvalidOperationException("小偏移基架子装配体缺少关闭标题：" + assemblyPath);
            CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("关闭小偏移基架子装窗口", baseAssemblyModel.GetPathName());
            _swApp.CloseDoc(baseTitle);
        }

        private void EnsureSmallOffsetCoverPlateDimensions(SolidWorksDriver driver, IModelDoc2 root)
        {
            UseDefaultModelConfiguration(root);
            var rootValues = new ReferencedModelValueReader(root);
            var width = rootValues.Global("基架宽度");
            var plans = FaceToFaceSmallOffsetFramePlanner.CreateCoverPlatePlans(width, rootValues.Global("基架长度"));
            var folderName = width > 1400d ? "双盖板" : "单盖板";
            var folder = FindFeatureByName(root, folderName)?.GetSpecificFeature2() as FeatureFolder;
            if (folder == null) throw new InvalidOperationException("小偏移盖板尺寸检查缺少文件夹：" + folderName);
            var children = (((IAssemblyDoc)root).GetComponents(true) as object[] ?? Array.Empty<object>()).OfType<IComponent2>().ToArray();
            var members = (folder.GetFeatures() as object[] ?? Array.Empty<object>()).OfType<Feature>().Select(feature =>
            {
                var matches = children.Where(component => ComponentInstanceMatchesFeature(component, feature.Name)).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("小偏移盖板组件无法唯一定位：" + folderName + "/" + feature.Name);
                return matches[0];
            }).ToArray();
            if (members.Length != plans.Sum(p => p.InstanceCount))
                throw new InvalidOperationException("小偏移盖板文件夹组件数量与模板不符：" + root.GetPathName() + "/" + folderName);
            var targets = new List<Tuple<IComponent2, FaceToFaceSmallOffsetCoverPlatePlan>>();
            foreach (var plan in plans)
            {
                var matches = members.Where(c => SmallOffsetSealPlateIdentifierMatches(c.GetPathName(), plan.ComponentPrefix)).ToArray();
                if (matches.Length != plan.InstanceCount || matches.Any(c => c.IsSuppressed()))
                    throw new InvalidOperationException("小偏移盖板的实例数量或解压状态不符：" + root.GetPathName() + "/" + plan.ComponentPrefix);
                foreach (var group in matches.GroupBy(c => c.GetPathName() + "\n" + c.ReferencedConfiguration, StringComparer.OrdinalIgnoreCase))
                    targets.Add(Tuple.Create(group.First(), plan));
            }
            var changed = false;
            foreach (var target in targets)
                changed = CorrectSmallOffsetCoverComponent(driver, root, target.Item1, target.Item2) || changed;
            if (changed && !root.EditRebuild3())
                throw new InvalidOperationException("小偏移盖板补写后基架重建失败：" + root.GetPathName());
            // 父装重建后再次反读，防止外部参照把已经补写的尺寸改回旧值。
            foreach (var target in targets)
            {
                var reader = ReferencedModelValueReader.FromComponent(target.Item1);
                target.Item2.RequireMatches(reader.Dimension);
            }
        }

        private bool CorrectSmallOffsetCoverComponent(SolidWorksDriver driver, IModelDoc2 root,
            IComponent2 component, FaceToFaceSmallOffsetCoverPlatePlan plan)
        {
            var model = driver.OpenOrActivateDocument(component.GetPathName(), rebuildAfterResolve: false);
            var configuration = UseDefaultModelConfiguration(model);
            RequireDefaultReference(model, component.ReferencedConfiguration, component.Name2);
            var reader = new ReferencedModelValueReader(model, configuration, component.Name2);
            if (plan.Matches(reader.Dimension)) return false;
            if (model.IsOpenedReadOnly()) throw new InvalidOperationException("小偏移盖板为只读，不能补写尺寸：" + model.GetPathName());
            using (var context = new SmallOffsetCoverConfigurationScope(_swApp, root, model, reader.Configuration))
            {
                try
                {
                    context.Activate();
                    reader = new ReferencedModelValueReader(model, reader.Configuration, model.GetPathName());
                    var changed = plan.CorrectIfNeeded(reader.Dimension, () =>
                    {
                        ApplySmallOffsetCoverEquations(model, reader.Configuration, plan);
                        ((IEquationMgr)model.GetEquationMgr()).EvaluateAll();
                        if (!model.ForceRebuild3(false) || !model.EditRebuild3())
                            throw new InvalidOperationException("小偏移盖板补写后重建失败：" + model.GetPathName());
                    });
                    if (changed)
                    {
                        var errors = 0; var warnings = 0;
                        if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings) || errors != 0)
                            throw new InvalidOperationException("小偏移盖板保存失败：" + model.GetPathName() + "；错误码=" + errors);
                    }
                    return changed;
                }
                catch (Exception error)
                {
                    context.Failure = error;
                    throw new InvalidOperationException("小偏移盖板尺寸处理失败：" + model.GetPathName() + "；配置=" + reader.Configuration, error);
                }
            }
        }

        private static void ApplySmallOffsetCoverEquations(IModelDoc2 model, string configuration, FaceToFaceSmallOffsetCoverPlatePlan plan)
        {
            var manager = (IEquationMgr)model.GetEquationMgr();
            foreach (var dimension in plan.Equations.Keys)
                if (FindSmallOffsetCoverEquation(manager, dimension) < 0)
                    throw new InvalidOperationException("小偏移盖板缺少原轮廓尺寸方程式：" + model.GetPathName() + "/" + dimension);
            var singleConfiguration = (model.GetConfigurationNames() as string[] ?? Array.Empty<string>()).Length == 1;
            // 草图20/21/2是从动外部参照，不能强改其尺寸或约束。仅在失配时为原轮廓公式换用局部输入。
            foreach (var pair in plan.Variables.OrderBy(p => p.Key, StringComparer.Ordinal))
                SetSmallOffsetCoverEquation(manager, configuration, singleConfiguration, pair.Key,
                    pair.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "mm", allowAdd: true);
            foreach (var pair in plan.Equations.OrderBy(p => p.Key, StringComparer.Ordinal))
                SetSmallOffsetCoverEquation(manager, configuration, singleConfiguration, pair.Key, pair.Value, allowAdd: false);
        }

        private static void SetSmallOffsetCoverEquation(IEquationMgr manager, string configuration, bool singleConfiguration,
            string name, string expression, bool allowAdd)
        {
            var index = FindSmallOffsetCoverEquation(manager, name);
            var equation = "\"" + name + "\" = " + expression;
            if (index < 0)
            {
                if (!allowAdd) throw new InvalidOperationException("小偏移盖板缺少尺寸方程式：" + name);
                index = singleConfiguration ? manager.Add2(0, equation, false)
                    : manager.Add3(0, equation, false, (int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { configuration });
                if (index < 0) throw new InvalidOperationException("小偏移盖板局部变量创建失败：" + name + "；配置=" + configuration);
            }
            else if (singleConfiguration) manager.Equation[index] = equation;
            else if (manager.SetEquationAndConfigurationOption(index, equation,
                (int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { configuration }) < 0)
                throw new InvalidOperationException("无法仅针对引用配置修改小偏移盖板方程式：" + name + "；配置=" + configuration);
        }

        private static int FindSmallOffsetCoverEquation(IEquationMgr manager, string name)
        {
            var found = -1;
            for (var index = 0; index < manager.GetCount(); index++)
            {
                var text = manager.Equation[index] ?? "";
                var equals = text.IndexOf('=');
                if (equals < 0 || !string.Equals(text.Substring(0, equals).Trim().Trim('"'), name, StringComparison.Ordinal)) continue;
                if (found >= 0) throw new InvalidOperationException("小偏移盖板方程式目标不唯一：" + name);
                found = index;
            }
            return found;
        }

        private sealed class SmallOffsetCoverConfigurationScope : IDisposable
        {
            private readonly ISldWorks _app;
            private readonly IModelDoc2 _root;
            private readonly IModelDoc2 _model;
            private readonly string _original;
            private readonly string _target;
            private readonly bool _switched;
            internal Exception Failure { get; set; }
            internal SmallOffsetCoverConfigurationScope(ISldWorks app, IModelDoc2 root, IModelDoc2 model, string configuration)
            {
                _app = app; _root = root; _model = model;
                _original = GetRequiredActiveConfigurationName(model, model.GetPathName(), root.GetPathName());
                _target = configuration;
                _switched = !string.Equals(_original, configuration, StringComparison.OrdinalIgnoreCase);
            }
            internal void Activate()
            {
                if (_switched && !_model.ShowConfiguration2(_target))
                    throw new InvalidOperationException("无法激活小偏移盖板引用配置：" + _model.GetPathName() + "/" + _target);
            }
            public void Dispose()
            {
                RestoreSmallOffsetConfigurationAndEditingContext(Failure,
                    _switched ? (Func<bool>)(() => _model.ShowConfiguration2(_original)) : null,
                    () => SWHelper.EnsureAssemblyEditingContext(_app, _root), _model.GetPathName(), _original, _root.GetPathName());
            }
        }

        internal static bool SmallOffsetSealPlateIdentifierMatches(
            string componentIdentifier,
            string drawingNumberPrefix)
        {
            var identifier = NormalizeComponentIdentifier(componentIdentifier);
            var prefix = NormalizeComponentIdentifier(drawingNumberPrefix);
            if (string.IsNullOrWhiteSpace(identifier)
                || string.IsNullOrWhiteSpace(prefix)
                || !identifier.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (identifier.Length == prefix.Length)
            {
                return true;
            }

            // 图号后只允许实例号或 PackAndGo 后缀等分隔符，避免把 2240 误认成 224*。
            return !char.IsLetterOrDigit(identifier[prefix.Length]);
        }

        internal static void EnsureUniqueSmallOffsetSealPlateMatch(
            int matchCount,
            string targetName,
            string assemblyPath)
        {
            if (matchCount == 1)
            {
                return;
            }

            throw new InvalidOperationException(
                $"小偏移基架角钢封板驱动失败，未找到唯一目标“{targetName}”；"
                + $"匹配数量={matchCount}；装配体={assemblyPath}");
        }

        internal const string SmallOffsetConfigurationRestoreWarningDataKey =
            "CKBusbarBridge.SmallOffsetConfigurationRestoreWarning";

        internal static void EnsureSmallOffsetSealPlateComponentResolved(
            bool isSuppressed,
            string targetName,
            string assemblyPath)
        {
            if (isSuppressed)
            {
                throw new InvalidOperationException(
                    $"小偏移基架角钢封板驱动失败，目标“{targetName}”仍处于压缩状态；"
                    + $"装配体={assemblyPath}");
            }
        }

        internal static void RestoreSmallOffsetConfigurationAndEditingContext(
            Exception operationException,
            Func<bool> restoreConfiguration,
            Action restoreEditingContext,
            string targetName,
            string originalConfiguration,
            string assemblyPath)
        {
            if (restoreEditingContext == null)
            {
                throw new ArgumentNullException(nameof(restoreEditingContext));
            }

            Exception cleanupFailure = null;
            try
            {
                if (restoreConfiguration != null)
                {
                    try
                    {
                        if (!restoreConfiguration())
                        {
                            cleanupFailure = new InvalidOperationException(
                                $"小偏移基架角钢封板驱动后无法恢复“{targetName}”配置"
                                + $"“{originalConfiguration}”；装配体={assemblyPath}");
                        }
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure = new InvalidOperationException(
                            $"小偏移基架角钢封板驱动后恢复“{targetName}”配置"
                            + $"“{originalConfiguration}”时发生异常；装配体={assemblyPath}",
                            exception);
                    }
                }
            }
            finally
            {
                // 配置恢复失败也必须回到根装编辑上下文，避免后续操作落到子件窗口。
                try
                {
                    restoreEditingContext();
                }
                catch (Exception exception)
                {
                    var contextFailure = new InvalidOperationException(
                        $"小偏移基架角钢封板驱动后恢复装配体编辑上下文失败："
                        + assemblyPath,
                        exception);
                    if (cleanupFailure == null)
                    {
                        cleanupFailure = contextFailure;
                    }
                    else
                    {
                        cleanupFailure = new AggregateException(
                            cleanupFailure,
                            contextFailure);
                    }
                }
            }

            if (cleanupFailure == null)
            {
                return;
            }

            if (operationException != null)
            {
                // 保留主驱动异常；配置或编辑上下文恢复问题作为附加诊断返回。
                var existingDiagnostic = operationException.Data[
                    SmallOffsetConfigurationRestoreWarningDataKey] as string;
                var cleanupDiagnostic = cleanupFailure.ToString();
                operationException.Data[SmallOffsetConfigurationRestoreWarningDataKey] =
                    string.IsNullOrWhiteSpace(existingDiagnostic)
                        ? cleanupDiagnostic
                        : existingDiagnostic + System.Environment.NewLine
                            + cleanupDiagnostic;
                return;
            }

            var invalidOperation = cleanupFailure as InvalidOperationException;
            if (invalidOperation != null)
            {
                throw invalidOperation;
            }

            throw new InvalidOperationException(
                "小偏移基架角钢封板驱动后的会话状态恢复失败。",
                cleanupFailure);
        }

        internal static string RequireSmallOffsetReferencedConfiguration(
            string referencedConfiguration,
            string nativeConfiguration,
            string targetName,
            string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(referencedConfiguration))
            {
                throw new InvalidOperationException(
                    $"小偏移基架角钢封板驱动失败，目标“{targetName}”引用配置为空；"
                    + $"装配体={assemblyPath}");
            }

            new NativeModelConfiguration().RequireReference(referencedConfiguration, nativeConfiguration, targetName + "；" + assemblyPath);
            return nativeConfiguration;
        }

        internal static void EnsureSmallOffsetSealPlateRebuildSucceeded(
            bool succeeded,
            string modelPath)
        {
            if (!succeeded)
            {
                throw new InvalidOperationException(
                    $"小偏移基架角钢封板模型重建失败：{modelPath}");
            }
        }

        internal static void EnsureSmallOffsetSealPlateSaveSucceeded(
            bool saved,
            int errors,
            string modelPath)
        {
            if (!saved || errors != 0)
            {
                throw new InvalidOperationException(
                    $"小偏移基架角钢封板模型保存失败：{modelPath}；错误码={errors}");
            }
        }

        private void DriveSmallOffsetSealPlateComponent(
            SolidWorksDriver driver,
            IModelDoc2 rootModel,
            SmallOffsetSealPlateRuntimeTarget target,
            double frameHeight,
            string assemblyPath)
        {
            var targetName = target.Plan.FolderName
                + "/"
                + target.Plan.PlateComponentPrefix
                + "*";

            var platePath = target.PlateComponent.GetPathName();
            if (string.IsNullOrWhiteSpace(platePath) || !File.Exists(platePath))
            {
                throw new FileNotFoundException(
                    $"小偏移基架角钢封板零件不存在：{targetName}；装配体={assemblyPath}",
                    platePath);
            }

            var plateModel = driver.OpenOrActivateDocument(platePath);
            if (plateModel == null
                || plateModel.GetType() != (int)swDocumentTypes_e.swDocPART)
            {
                throw new InvalidOperationException(
                    $"小偏移基架角钢封板驱动失败，目标不是零件：{platePath}");
            }

            var referencedConfiguration = RequireSmallOffsetReferencedConfiguration(
                target.PlateComponent.ReferencedConfiguration,
                NativeDefaultModelConfiguration(plateModel), targetName, assemblyPath);
            UseDefaultModelConfiguration(plateModel);
            var originalConfiguration = GetRequiredActiveConfigurationName(
                plateModel,
                targetName,
                assemblyPath);
            var switchedConfiguration = !string.Equals(
                referencedConfiguration,
                originalConfiguration,
                StringComparison.OrdinalIgnoreCase);
            Exception operationException = null;
            try
            {
                if (switchedConfiguration
                    && !plateModel.ShowConfiguration2(referencedConfiguration))
                {
                    throw new InvalidOperationException(
                        $"小偏移基架角钢封板驱动失败，无法激活“{targetName}”引用配置"
                        + $"“{referencedConfiguration}”；装配体={assemblyPath}");
                }

                // 零件同名变量接收完整柜体偏移值；032主装的孔偏移变量仍只取一半。
                driver.ApplyGlobalVariablesRequired(
                    plateModel,
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["基架高度"] = frameHeight,
                        [target.Plan.OffsetVariableName] = target.Plan.PlateWidth
                    },
                    rebuildAfterChange: false);
                EnsureSmallOffsetSealPlateRebuildSucceeded(
                    plateModel.EditRebuild3(),
                    platePath);
                RequireSmallOffsetGlobal(plateModel, "基架高度", frameHeight);
                RequireSmallOffsetGlobal(plateModel, target.Plan.OffsetVariableName, target.Plan.PlateWidth);

                var errors = 0;
                var warnings = 0;
                var saved = plateModel.Save3(
                    (int)(swSaveAsOptions_e.swSaveAsOptions_Silent
                        | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                    ref errors,
                    ref warnings);
                EnsureSmallOffsetSealPlateSaveSucceeded(saved, errors, platePath);
            }
            catch (Exception exception)
            {
                operationException = exception;
                throw;
            }
            finally
            {
                RestoreSmallOffsetConfigurationAndEditingContext(
                    operationException,
                    switchedConfiguration
                        ? (Func<bool>)(() => plateModel.ShowConfiguration2(
                            originalConfiguration))
                        : null,
                    () => SWHelper.EnsureAssemblyEditingContext(_swApp, rootModel),
                    targetName,
                    originalConfiguration,
                    assemblyPath);
            }
        }

        private static string GetRequiredActiveConfigurationName(
            IModelDoc2 model,
            string targetName,
            string assemblyPath)
        {
            var activeConfigurationName = model
                ?.ConfigurationManager
                ?.ActiveConfiguration
                ?.Name;
            if (string.IsNullOrWhiteSpace(activeConfigurationName))
            {
                throw new InvalidOperationException(
                    $"小偏移基架角钢封板驱动失败，无法读取“{targetName}”当前配置；"
                    + $"装配体={assemblyPath}");
            }

            return activeConfigurationName;
        }

        private static void ApplySealPlateFolderSuppression(
            IModelDoc2 baseAssemblyModel,
            FaceToFaceSmallOffsetSealPlateDrivePlan plan,
            string assemblyPath)
        {
            var duplicateFolder = plan.Folders
                .GroupBy(folder => folder.FolderName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() != 1);
            if (duplicateFolder != null)
            {
                throw new InvalidOperationException(
                    $"小偏移基架角钢封板计划包含重复文件夹：{duplicateFolder.Key}");
            }

            // 先确认四个文件夹都存在，再按文件夹整体切换，不依赖尚未解压的子件来定位。
            var folders = plan.Folders.Select(folderPlan => new
            {
                Plan = folderPlan,
                Feature = FindFeatureByName(baseAssemblyModel, folderPlan.FolderName)
            }).ToList();
            foreach (var folder in folders)
            {
                if (!(folder.Feature?.GetSpecificFeature2() is FeatureFolder))
                    throw new InvalidOperationException(
                        $"小偏移基架角钢封板驱动失败，未找到可处理文件夹“{folder.Plan.FolderName}”；装配体={assemblyPath}");
            }

            foreach (var folder in folders)
            {
                var suppressFolder = folder.Plan.SuppressAngleSteel;
                if (SealPlateFolderMatchesSuppression(
                    baseAssemblyModel, folder.Feature, suppressFolder, assemblyPath))
                    continue;

                baseAssemblyModel.ClearSelection2(true);
                try
                {
                    if (!folder.Feature.Select2(false, -1))
                        throw new InvalidOperationException(
                            $"小偏移基架角钢封板驱动失败，无法选择文件夹“{folder.Plan.FolderName}”；装配体={assemblyPath}");
                    var succeeded = suppressFolder
                        ? baseAssemblyModel.EditSuppress2()
                        : baseAssemblyModel.EditUnsuppress2();
                    if (!succeeded)
                        throw new InvalidOperationException(
                            $"小偏移基架角钢封板文件夹“{folder.Plan.FolderName}”"
                            + $"{(suppressFolder ? "压缩" : "解压")}失败；装配体={assemblyPath}");
                }
                finally
                {
                    baseAssemblyModel.ClearSelection2(true);
                }
            }
        }

        private static void SetSmallOffsetNotchSuppression(Feature feature, bool suppress, string path)
        {
            var states = feature.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) as bool[];
            if (states?.Length == 1 && states[0] == suppress) return;
            if (!feature.SetSuppression2((int)(suppress ? swFeatureSuppressionAction_e.swSuppressFeature
                    : swFeatureSuppressionAction_e.swUnSuppressFeature),
                    (int)swInConfigurationOpts_e.swThisConfiguration, null))
                throw new InvalidOperationException("小偏移角钢豁口状态切换失败：" + path);
            ValidateSmallOffsetNotchSuppression(feature, suppress, path);
        }

        private static void ValidateSmallOffsetNotchSuppression(Feature feature, bool suppress, string path)
        {
            var states = feature.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) as bool[];
            if (states?.Length != 1 || states[0] != suppress)
                throw new InvalidOperationException("小偏移角钢豁口状态校验失败：" + path);
        }

        private static void RequireSmallOffsetGlobal(IModelDoc2 model, string name, double expected)
        {
            if (!CabinetAssemblyDimensionReader.TryGetGlobalVariableValue(model, name, out var actual)
                || !(Math.Abs(actual - expected) <= .001))
                throw new InvalidOperationException("小偏移驱动变量校验失败：" + model.GetPathName() + "/" + name);
        }

        private static void SetSmallOffsetComponentSuppression(IComponent2 component, bool suppress, string path)
        {
            if (component.IsSuppressed() != suppress)
                component.SetSuppression2((int)(suppress ? swComponentSuppressionState_e.swComponentSuppressed
                    : swComponentSuppressionState_e.swComponentResolved));
            if (component.IsSuppressed() != suppress)
                throw new InvalidOperationException("小偏移组件状态切换失败：" + component.Name2 + "；基架=" + path);
        }

        private static void ValidateSmallOffsetSealComponents(IModelDoc2 baseModel,
            FaceToFaceSmallOffsetSealPlateDrivePlan plan, string path)
        {
            var children = (((IAssemblyDoc)baseModel).GetComponents(true) as object[] ?? Array.Empty<object>())
                .OfType<IComponent2>().ToArray();
            foreach (var folder in plan.Folders.Where(f => f.Suppress))
                if (!SealPlateFolderMatchesSuppression(baseModel, FindFeatureByName(baseModel, folder.FolderName), true, path))
                    throw new InvalidOperationException("非活动封板侧仍有解压组件：" + folder.FolderName);
            foreach (var target in ResolveSealPlateFolderTargets(baseModel, children, plan, path))
                foreach (var component in target.Components)
                {
                    var expected = !ComponentIdentifierMatches(component, target.Plan.PlateComponentPrefix)
                        && target.Plan.SuppressAngleSteel;
                    if (component.IsSuppressed() != expected)
                        throw new InvalidOperationException("小偏移封板组件状态校验失败：" + component.Name2);
                }
        }

        private static bool SealPlateFolderMatchesSuppression(
            IModelDoc2 baseAssemblyModel, Feature folderFeature, bool expectedSuppressed, string assemblyPath)
        {
            var featureFolder = folderFeature.GetSpecificFeature2() as FeatureFolder;
            var children = (featureFolder?.GetFeatures() as object[])
                ?.OfType<Feature>().ToList() ?? new List<Feature>();
            if (children.Count == 0)
                throw new InvalidOperationException(
                    $"小偏移基架角钢封板文件夹“{folderFeature.Name}”为空，无法检查压缩状态；装配体={assemblyPath}");

            // Reference 特征的压缩状态不代表组件状态；GetComponents 能枚举尚未解压的组件。
            var components = (((IAssemblyDoc)baseAssemblyModel).GetComponents(true) as object[])
                ?.OfType<IComponent2>().ToList() ?? new List<IComponent2>();
            var matches = true;
            foreach (var child in children)
            {
                var childComponents = components.Where(component =>
                    ComponentInstanceMatchesFeature(component, child.Name)).ToList();
                EnsureUniqueSmallOffsetSealPlateMatch(childComponents.Count,
                    folderFeature.Name + "/" + child.Name, assemblyPath);
                matches &= childComponents[0].IsSuppressed() == expectedSuppressed;
            }
            return matches;
        }

        private static IReadOnlyList<SmallOffsetSealPlateRuntimeTarget>
            ResolveSealPlateFolderTargets(
                IModelDoc2 baseAssemblyModel,
                IReadOnlyList<IComponent2> directChildren,
                FaceToFaceSmallOffsetSealPlateDrivePlan plan,
                string assemblyPath)
        {
            var targets = new List<SmallOffsetSealPlateRuntimeTarget>(plan.Folders.Count);
            foreach (var folderPlan in plan.Folders.Where(folder => !folder.Suppress))
            {
                var folderFeature = FindFeatureByName(
                    baseAssemblyModel,
                    folderPlan.FolderName);
                var featureFolder = folderFeature?.GetSpecificFeature2() as FeatureFolder;
                if (featureFolder == null)
                {
                    throw new InvalidOperationException(
                        $"小偏移基架角钢封板驱动失败，未找到可处理文件夹"
                        + $"“{folderPlan.FolderName}”；装配体={assemblyPath}");
                }

                var childFeatures = (featureFolder.GetFeatures() as object[])
                    ?.OfType<Feature>()
                    .ToList()
                    ?? new List<Feature>();
                if (childFeatures.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"小偏移基架角钢封板文件夹“{folderPlan.FolderName}”为空；"
                        + $"装配体={assemblyPath}");
                }

                var folderComponents = new List<IComponent2>(childFeatures.Count);
                foreach (var childFeature in childFeatures)
                {
                    var matches = directChildren
                        .Where(component => ComponentInstanceMatchesFeature(
                            component,
                            childFeature.Name))
                        .ToList();
                    EnsureUniqueSmallOffsetSealPlateMatch(
                        matches.Count,
                        folderPlan.FolderName + "/" + childFeature.Name,
                        assemblyPath);
                    folderComponents.Add(matches[0]);
                }

                IComponent2 plateComponent = null;
                if (!folderPlan.Suppress)
                {
                    var plateMatches = folderComponents
                        .Where(component => ComponentIdentifierMatches(
                            component,
                            folderPlan.PlateComponentPrefix))
                        .ToList();
                    EnsureUniqueSmallOffsetSealPlateMatch(
                        plateMatches.Count,
                        folderPlan.FolderName + "/" + folderPlan.PlateComponentPrefix + "*",
                        assemblyPath);
                    plateComponent = plateMatches[0];
                    EnsureUniqueSmallOffsetSealPlateMatch(folderComponents.Count(component =>
                        ComponentIdentifierMatches(component, "8GX.160.167")),
                        folderPlan.FolderName + "/8GX.160.167*", assemblyPath);
                }

                targets.Add(new SmallOffsetSealPlateRuntimeTarget(
                    folderPlan,
                    folderComponents,
                    plateComponent));
            }

            return targets;
        }

        internal static bool ComponentIdentifierMatches(
            IComponent2 component,
            string drawingNumberPrefix)
        {
            if (component == null)
            {
                return false;
            }

            string name = null;
            string path = null;
            try { name = component.Name2; } catch { }
            try { path = component.GetPathName(); } catch { }
            return SmallOffsetSealPlateIdentifierMatches(name, drawingNumberPrefix)
                || SmallOffsetSealPlateIdentifierMatches(path, drawingNumberPrefix);
        }

        private static bool ComponentInstanceMatchesFeature(
            IComponent2 component,
            string featureName)
        {
            if (component == null || string.IsNullOrWhiteSpace(featureName))
            {
                return false;
            }

            string componentName = null;
            try { componentName = component.Name2; } catch { }
            return string.Equals(
                NormalizeComponentIdentifier(componentName),
                NormalizeComponentIdentifier(featureName),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeComponentIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return string.Empty;
            }

            var normalized = identifier.Trim();
            var atIndex = normalized.IndexOf('@');
            if (atIndex >= 0)
            {
                normalized = normalized.Substring(0, atIndex);
            }

            var separatorIndex = normalized.LastIndexOfAny(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (separatorIndex >= 0 && separatorIndex < normalized.Length - 1)
            {
                normalized = normalized.Substring(separatorIndex + 1);
            }

            foreach (var extension in new[] { ".SLDASM", ".SLDPRT" })
            {
                if (normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Substring(0, normalized.Length - extension.Length);
                    break;
                }
            }

            return normalized.Trim();
        }

        private static Feature FindFeatureByName(IModelDoc2 model, string featureName)
        {
            var featureObject = model?.FirstFeature();
            while (featureObject != null)
            {
                var feature = featureObject as Feature;
                var match = FindFeatureByName(feature, featureName);
                if (match != null)
                {
                    return match;
                }

                featureObject = feature?.GetNextFeature();
            }

            return null;
        }

        private static Feature FindFeatureByName(Feature feature, string featureName)
        {
            if (feature == null)
            {
                return null;
            }

            if (string.Equals(feature.Name, featureName, StringComparison.OrdinalIgnoreCase))
            {
                return feature;
            }

            var subFeatureObject = feature.GetFirstSubFeature();
            while (subFeatureObject != null)
            {
                var subFeature = subFeatureObject as Feature;
                var match = FindFeatureByName(subFeature, featureName);
                if (match != null)
                {
                    return match;
                }

                subFeatureObject = subFeature?.GetNextSubFeature();
            }

            return null;
        }

        private sealed class SmallOffsetSealPlateRuntimeTarget
        {
            internal SmallOffsetSealPlateRuntimeTarget(
                FaceToFaceSmallOffsetSealPlateFolderPlan plan,
                IReadOnlyList<IComponent2> components,
                IComponent2 plateComponent)
            {
                Plan = plan;
                Components = components;
                PlateComponent = plateComponent;
            }

            internal FaceToFaceSmallOffsetSealPlateFolderPlan Plan { get; }

            internal IReadOnlyList<IComponent2> Components { get; }

            internal IComponent2 PlateComponent { get; }
        }

        public void SaveModel(string assemblyPath)
        {
            var driver = new SolidWorksDriver(_swApp);
            var model = driver.OpenOrActivateDocument(assemblyPath);
            UseDefaultModelConfiguration(model);
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
                    $"保存二次驱动后的基架模型失败：{assemblyPath}；错误码={errors}");
            }
        }

        public void WriteCustomProperty(
            string assemblyPath,
            string propertyName,
            string value)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw new ArgumentException("模型属性名不能为空。", nameof(propertyName));
            }

            var driver = new SolidWorksDriver(_swApp);
            var model = driver.OpenOrActivateDocument(assemblyPath);
            UseDefaultModelConfiguration(model);
            driver.SetCustomProperty(model, propertyName, value ?? string.Empty);
        }

        public void PackAndGoWithSuffix(
            string assemblyPath,
            string outputDirectory,
            string suffix)
        {
            var concreteApp = _swApp as SldWorks;
            if (concreteApp == null)
            {
                throw new InvalidOperationException(
                    "当前 SolidWorks 应用对象不支持 PackAndGo。");
            }

            new SolidWorksFileHelper(concreteApp).CopyFileUsePackAndGoWithSuffix(
                assemblyPath,
                outputDirectory,
                suffix,
                new List<string>());
        }

        public FaceToFaceFrameCleanupConfirmation CloseDocumentsUnderDirectory(
            string directoryPath)
        {
            return new FaceToFaceFrameScopedDocumentCleaner(
                new SolidWorksFaceToFaceFrameDocumentSession(_swApp))
                .CloseDocumentsUnderDirectory(directoryPath);
        }

        public FaceToFaceFrameCleanupConfirmation RequestCloseDocumentsUnderDirectory(
            string directoryPath)
        {
            return new FaceToFaceFrameScopedDocumentCleaner(
                new SolidWorksFaceToFaceFrameDocumentSession(_swApp))
                .RequestCloseDocumentsUnderDirectory(directoryPath);
        }
    }
}
