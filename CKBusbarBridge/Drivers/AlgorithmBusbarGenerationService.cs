using UiFeedback = CKBusbarBridgePreview.UiFeedback;
using CKBusbarBridgePreview;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.models;
using CKBusbarBridge.Services;
using CKBusbarBridge.viewmodels;
using GeometRi;
using Microsoft.Win32;
using NLog;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 将 Worker 方案的中心线铜排转换为自由折弯零件，并把所有铜排
    /// 生成到同一个模板子装配体，再把完整子装配体插入总装。
    /// 铜排在子装配体中保留总装坐标，子装配体以原点插入后位置不变。
    /// </summary>
    internal sealed partial class AlgorithmBusbarGenerationService
    {
        private const string BusbarOutputFolderName = "铜排";
        private const string FreeBendTemplateFolderName = "FreeBendBar";
        private const string BusbarTemplateRootFolderName = "BusbarTemplates";
        private const string GenerationAssemblyTemplateName = "装配体模板";
        private const string PlanarBendTemplateName = "平弯模板";
        private const string VerticalBendTemplateName = "自由立弯";
        private const string StraightBusbarTemplateName = "I";
        private const string StraightMilledBusbarTemplateName = "铣排模板I";
        private const string BridgeMilledBusbarTemplateName = "桥式铣排";
        private const string CopperBarLinkageAddinClassId =
            "{6882B6C9-5653-4D5D-B80D-EC31EE5B7544}";
        private const string PartTypePropertyName = "零件类型";
        private const string BusbarPartTypePropertyValue = "铜排";
        private const string BusbarShapePropertyName = "铜排形状";
        private const string BusbarBendTypePropertyName = "折弯类型";
        private const string BusbarPhasePropertyName = "铜排相序";
        private const string BusbarSpecificationPropertyName = "铜排规格";
        private const string BusbarBendingRadiusPropertyName = "铜排折弯半径";
        private const string DefaultFixedPropertyName = "默认固定";
        private const string DrawingNumberPropertyName = "图号";
        private const string DesignerPropertyName = "设计";
        private const string ProofreaderPropertyName = "校对";
        private const string ReviewerPropertyName = "审核";
        private const string ApproverPropertyName = "批准";
        private const double DirectionTolerance = 0.000001d;
        private const double SameSourcePointToleranceMillimeters = 0.1d;
        private const double SketchPointMatchToleranceMeters = 0.0000001d;

        private static readonly Logger GenerationLogger =
            LogManager.GetCurrentClassLogger();

        private readonly ISldWorks _solidWorksApplication;
        private readonly BridgeCopperGenerationContext _bridgeContext;

        // 图形刷新抑制状态快照：进入抑制时保存，恢复时还原。
        private bool _modelViewGraphicsSuspended;
        private bool _modelViewOldSuppressWaitCursorDuringRedraw;
        private bool _modelViewOldEnableGraphicsUpdate;
        private bool _modelViewOldEnableFeatureTreeWindow;
        private bool _modelViewOldEnableFeatureTree;
        private ModelView _modelViewSuppressedModelView;
        private ModelDoc2 _modelViewSuppressedDocument;

        public AlgorithmBusbarGenerationService(ISldWorks solidWorksApplication)
            : this(solidWorksApplication, null) { }

        internal AlgorithmBusbarGenerationService(ISldWorks solidWorksApplication, BridgeCopperGenerationContext bridgeContext)
        {
            _solidWorksApplication = solidWorksApplication ??
                throw new ArgumentNullException(nameof(solidWorksApplication));
            _bridgeContext = bridgeContext;
        }

        internal int ValidateBridgeInputs(IReadOnlyList<AlgorithmBusbarGenerationGroup> groups)
        {
            var work = BuildGenerationWorkGroups(groups);
            var templateDirectory = ResolveFreeBendTemplateDirectory();
            foreach (var name in work.SelectMany(g => g.GenerationItems).Select(i => i.TemplateName).Distinct())
                if (!File.Exists(Path.Combine(templateDirectory, name + ".SLDPRT")))
                    throw new FileNotFoundException("缺少铜排生成模板：" + name);
            if (!File.Exists(Path.Combine(templateDirectory, GenerationAssemblyTemplateName + ".SLDASM")))
                throw new FileNotFoundException("缺少铜排总成模板。");
            if (!SolidWorksMaterialHelper.TryResolveBusbarMaterialDatabasePath(_solidWorksApplication, out _, out var materialError))
                throw new InvalidOperationException(materialError);
            return work.Sum(g => g.GenerationItems.Count);
        }

        public AlgorithmBusbarGenerationResult Generate(
            IReadOnlyList<AlgorithmBusbarGenerationGroup>
                centerlineGenerationGroups,
            string busbarMaterial)
        {
            var generationResult = new AlgorithmBusbarGenerationResult();
            var normalizedBusbarMaterial = SolidWorksMaterialHelper
                .NormalizeBusbarMaterial(busbarMaterial);
            var activeAssemblyModel = _solidWorksApplication.IActiveDoc2 as ModelDoc2;
            _bridgeContext?.Validate(activeAssemblyModel);
            var activeAssemblyDocument = activeAssemblyModel as AssemblyDoc;
            if (activeAssemblyModel == null || activeAssemblyDocument == null ||
                activeAssemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                generationResult.Diagnostics.Add(
                    "请先激活需要生成母排的 SolidWorks 装配体。");
                return generationResult;
            }

            var activeAssemblyPath = activeAssemblyModel.GetPathName();
            if (string.IsNullOrWhiteSpace(activeAssemblyPath))
            {
                generationResult.Diagnostics.Add(
                    "当前装配体尚未保存，无法确定铜排输出目录。");
                return generationResult;
            }

            var sourceGenerationGroups = (centerlineGenerationGroups ??
                    Array.Empty<AlgorithmBusbarGenerationGroup>())
                .Where(generationGroup =>
                    generationGroup != null &&
                    generationGroup.Busbars != null &&
                    generationGroup.Busbars.Count > 0)
                .ToList();
            generationResult.RequestedAssemblyCount =
                sourceGenerationGroups.Count > 0 ? 1 : 0;
            if (sourceGenerationGroups.Count == 0)
            {
                generationResult.Diagnostics.Add(
                    "当前方案没有可生成的铜排数据。");
                return generationResult;
            }

            var drawingNumberDriver =
                new SolidWorksDriver(_solidWorksApplication);
            if (!DrawingNumberMetadataHelper.TryRead(
                    drawingNumberDriver,
                    activeAssemblyModel,
                    out _,
                    out var drawingNumberMetadata) ||
                drawingNumberMetadata == null)
            {
                generationResult.Diagnostics.Add(
                    "当前总装配体缺少有效的“图号生成信息”，无法分配母排名称。");
                return generationResult;
            }

            if (!DrawingNumberHelper.TryParse(
                    _bridgeContext?.DrawingNumbers.PeekNextDrawingNumber() ?? drawingNumberMetadata.DrawingNumber,
                    out var drawingNumberContract,
                    out _))
            {
                generationResult.Diagnostics.Add(
                    "图号生成信息中的图号格式无效，应为“合同号.四位流水号”。");
                return generationResult;
            }

            var normalizedMetadataContract =
                drawingNumberMetadata.ContractNumber?.Trim()
                    .ToUpperInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedMetadataContract) ||
                !string.Equals(
                    normalizedMetadataContract,
                    drawingNumberContract,
                    StringComparison.OrdinalIgnoreCase))
            {
                generationResult.Diagnostics.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    "图号生成信息中的合同号与图号前缀不一致：合同号={0}，图号={1}。",
                    string.IsNullOrWhiteSpace(normalizedMetadataContract)
                        ? "空"
                        : normalizedMetadataContract,
                    drawingNumberMetadata.DrawingNumber));
                return generationResult;
            }

            List<AlgorithmBusbarGenerationWorkGroup> generationWorkGroups;
            try
            {
                generationWorkGroups = BuildGenerationWorkGroups(
                    sourceGenerationGroups);
                generationResult.RequestedBusbarCount = generationWorkGroups
                    .Sum(generationWorkGroup =>
                        generationWorkGroup.GenerationItems.Count);
            }
            catch (Exception generationPlanException)
            {
                generationResult.Diagnostics.Add(
                    "母排生成命名计划无效：" +
                    generationPlanException.Message);
                GenerationLogger.Error(
                    generationPlanException,
                    "母排生成命名与分组计划创建失败。");
                return generationResult;
            }

            string templateDirectory;
            try
            {
                templateDirectory = ResolveFreeBendTemplateDirectory();
                foreach (var requiredTemplateName in generationWorkGroups
                    .SelectMany(generationWorkGroup =>
                        generationWorkGroup.GenerationItems)
                    .Select(generationItem => generationItem.TemplateName)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var requiredTemplatePath = Path.Combine(
                        templateDirectory,
                        requiredTemplateName + ".SLDPRT");
                    if (!File.Exists(requiredTemplatePath))
                    {
                        throw new FileNotFoundException(
                            "未找到铜排生成模板。",
                            requiredTemplatePath);
                    }
                }

                var requiredAssemblyTemplatePath = Path.Combine(
                    templateDirectory,
                    GenerationAssemblyTemplateName + ".SLDASM");
                if (!File.Exists(requiredAssemblyTemplatePath))
                {
                    throw new FileNotFoundException(
                        "未找到铜排子装配体模板。",
                        requiredAssemblyTemplatePath);
                }
            }
            catch (Exception templateException)
            {
                generationResult.Diagnostics.Add(templateException.Message);
                GenerationLogger.Error(
                    templateException,
                    "母排生成模板定位失败。");
                return generationResult;
            }

            var activeAssemblyDirectory = Path.GetDirectoryName(activeAssemblyPath);
            if (string.IsNullOrWhiteSpace(activeAssemblyDirectory))
            {
                generationResult.Diagnostics.Add(
                    "无法取得当前装配体所在目录。");
                return generationResult;
            }

            generationResult.OutputDirectoryPath = Path.Combine(
                activeAssemblyDirectory,
                BusbarOutputFolderName);
            Directory.CreateDirectory(generationResult.OutputDirectoryPath);

            try
            {
                AssignAvailableGenerationOutputNames(
                    generationWorkGroups,
                    _bridgeContext?.DrawingNumbers.PeekNextDrawingNumber() ?? drawingNumberMetadata.DrawingNumber,
                    generationResult.OutputDirectoryPath,
                    activeAssemblyDocument);
                ValidateGenerationOutputPaths(
                    generationWorkGroups,
                    generationResult.OutputDirectoryPath,
                    activeAssemblyDocument);
            }
            catch (Exception outputValidationException)
            {
                generationResult.Diagnostics.Add(
                    outputValidationException.Message);
                GenerationLogger.Error(
                    outputValidationException,
                    "母排生成输出名称分配或校验失败。");
                return generationResult;
            }

            var generationItemCount = generationWorkGroups.Sum(
                generationWorkGroup =>
                    generationWorkGroup.GenerationItems.Count);
            var estimatedDurationTypes = generationWorkGroups
                .SelectMany(generationWorkGroup =>
                    generationWorkGroup.GenerationItems.Select(
                        generationItem => generationItem.DurationType))
                .Concat(Enumerable.Repeat(
                    BusbarGenerationDurationType.SubassemblyInsertion,
                    generationWorkGroups.Count));
            var estimatedGenerationDurationMilliseconds =
                BusbarGenerationDurationProfileStore
                    .EstimateTotalDurationMilliseconds(
                        estimatedDurationTypes);
            var elapsedMillisecondsByDurationType =
                new Dictionary<BusbarGenerationDurationType, List<long>>();
            var totalGenerationStepCount =
                generationItemCount + generationWorkGroups.Count;
            var completedGenerationStepCount = 0;
            var generationAttempted = false;
            var highestConsumedDrawingNumberOffset = -1;
            return UiFeedback.Run("母排生成", generationProgressToast =>
            {
                try
                {
                    ReportGenerationToast(
                        generationProgressToast,
                        "正在准备母排生成",
                        completedGenerationStepCount,
                        totalGenerationStepCount,
                        string.Format(
                            CultureInfo.CurrentCulture,
                            "共 1 个子装配体、{0} 根铜排",
                            generationItemCount),
                        estimatedGenerationDurationMilliseconds);
                    EnableAcceleratedGenerationMode();
                    foreach (var generationWorkGroup in generationWorkGroups)
                    {
                        generationAttempted = true;
                        highestConsumedDrawingNumberOffset = Math.Max(
                            highestConsumedDrawingNumberOffset,
                            generationWorkGroup.DrawingNumberOffset);
                        ReportGenerationToast(
                            generationProgressToast,
                            "正在创建铜排子装配体",
                            completedGenerationStepCount,
                            totalGenerationStepCount,
                            generationWorkGroup.TargetAssemblyBaseName,
                            estimatedGenerationDurationMilliseconds);
                        TemplateGenerationAssemblyContext
                            generationAssemblyContext = null;
                        var generationSubassemblyComplete = false;
                        try
                        {
                            generationAssemblyContext =
                                new TemplateGenerationAssemblyContext();
                            CreateTemplateGenerationAssembly(
                                generationResult.OutputDirectoryPath,
                                templateDirectory,
                                generationWorkGroup,
                                drawingNumberMetadata,
                                generationAssemblyContext);

                            // 图形刷新抑制对普通直排/平弯/桥式直排有效，但铣排、
                            // 立弯模板的生成依赖实时图形与特征树刷新，抑制后可能
                            // 出错。因此按类型排序：普通排先（保持抑制加速），铣排、
                            // 立弯放最后（生成前恢复图形刷新）。
                            var orderedGenerationItems =
                                OrderGenerationItemsForAcceleratedGeneration(
                                    generationWorkGroup.GenerationItems);
                            var graphicsUpdateSuspended = false;
                            try
                            {
                                foreach (var generationItem in
                                    orderedGenerationItems)
                                {
                                    if (!RequiresGraphicsUpdate(generationItem))
                                    {
                                        if (!graphicsUpdateSuspended)
                                        {
                                            SuspendModelViewGraphics();
                                            graphicsUpdateSuspended = true;
                                        }
                                    }
                                    else if (graphicsUpdateSuspended)
                                    {
                                        ResumeModelViewGraphics();
                                        graphicsUpdateSuspended = false;
                                    }

                                    highestConsumedDrawingNumberOffset = Math.Max(
                                        highestConsumedDrawingNumberOffset,
                                        generationItem.DrawingNumberOffset);
                                    ReportGenerationToast(
                                        generationProgressToast,
                                        "正在生成母排零件",
                                        completedGenerationStepCount,
                                        totalGenerationStepCount,
                                        string.Format(
                                            CultureInfo.CurrentCulture,
                                            "{0}：{1}",
                                            generationWorkGroup.DisplayName,
                                            generationItem.TargetPartBaseName),
                                        estimatedGenerationDurationMilliseconds);
                                    try
                                    {
                                        var busbarGenerationStopwatch =
                                            Stopwatch.StartNew();
                                        var createdBusbarComponent =
                                            ReuseOrCreateBusbar(
                                                generationAssemblyContext
                                                    .AssemblyModel,
                                                generationAssemblyContext
                                                    .AssemblyDocument,
                                                generationAssemblyContext
                                                    .AssemblyPath,
                                                generationResult
                                                    .OutputDirectoryPath,
                                                templateDirectory,
                                                generationItem,
                                                drawingNumberMetadata,
                                                generationAssemblyContext
                                                    .OwnedOutputPaths);
                                        var createdPartPath =
                                            createdBusbarComponent?.GetPathName();
                                        if (string.IsNullOrWhiteSpace(
                                                createdPartPath))
                                        {
                                            throw new InvalidOperationException(
                                                "铜排模板生成后没有返回有效零件路径。");
                                        }

                                        generationAssemblyContext.CreatedPartPaths
                                            .Add(createdPartPath);
                                        generationResult.Parts[generationItem.BridgeKey] = BindPart(generationItem, createdBusbarComponent);
                                        busbarGenerationStopwatch.Stop();
                                        if (!elapsedMillisecondsByDurationType
                                                .TryGetValue(
                                                    generationItem.DurationType,
                                                    out var durationTypeSamples))
                                        {
                                            durationTypeSamples = new List<long>();
                                            elapsedMillisecondsByDurationType.Add(
                                                generationItem.DurationType,
                                                durationTypeSamples);
                                        }

                                        durationTypeSamples.Add(Math.Max(
                                            1L,
                                            busbarGenerationStopwatch
                                                .ElapsedMilliseconds));
                                        GenerationLogger.Info(
                                            "母排生成完成：{0}，类型={1}，耗时={2}ms，路径={3}",
                                            generationItem.TargetPartBaseName,
                                            BusbarGenerationDurationProfileStore
                                                .ResolveDurationTypeDisplayName(
                                                    generationItem.DurationType),
                                            busbarGenerationStopwatch
                                                .ElapsedMilliseconds,
                                            createdPartPath);
                                    }
                                    catch (Exception busbarGenerationException)
                                    {
                                        var verticalBendPointSummary =
                                            generationItem.VerticalBendSetting ==
                                            null
                                                ? "非立弯"
                                                : string.Join(
                                                    ";",
                                                    generationItem
                                                        .VerticalBendSetting
                                                        .PointList
                                                        .Select((point, pointIndex) =>
                                                            "P" +
                                                            (pointIndex + 1) +
                                                            "=" + point));
                                        FaceToWallPreviewTrace.Error(
                                            "母排正式生成-单根铜排失败",
                                            busbarGenerationException,
                                            "组=" +
                                            generationWorkGroup.DisplayName +
                                            "，零件=" +
                                            generationItem.TargetPartBaseName +
                                            "，模板=" +
                                            generationItem.TemplateName +
                                            "，立弯=" +
                                            (generationItem.VerticalBendSetting !=
                                             null) +
                                            "，点=" +
                                            verticalBendPointSummary);
                                        throw;
                                    }
                                    finally
                                    {
                                        completedGenerationStepCount++;
                                    }
                                }
                            }
                            finally
                            {
                                // 强制恢复图形刷新，避免异常时界面被抑制导致残留 BUG。
                                if (graphicsUpdateSuspended)
                                {
                                    ResumeModelViewGraphics();
                                }
                            }

                            ApplyGeneratedBusbarMaterials(
                                generationAssemblyContext.AssemblyModel,
                                normalizedBusbarMaterial,
                                generationWorkGroup.DisplayName);

                            ReportGenerationToast(
                                generationProgressToast,
                                "正在保存并插入铜排子装配体",
                                completedGenerationStepCount,
                                totalGenerationStepCount,
                                generationWorkGroup.TargetAssemblyBaseName,
                                estimatedGenerationDurationMilliseconds);
                            var subassemblyInsertionStopwatch =
                                Stopwatch.StartNew();
                            var createdAssemblyComponent =
                                SaveAndInsertTemplateGenerationAssembly(
                                    activeAssemblyModel,
                                    activeAssemblyDocument,
                                    activeAssemblyPath,
                                    generationAssemblyContext,
                                    generationWorkGroup);
                            subassemblyInsertionStopwatch.Stop();
                            if (!elapsedMillisecondsByDurationType.TryGetValue(
                                    BusbarGenerationDurationType
                                        .SubassemblyInsertion,
                                    out var subassemblyInsertionSamples))
                            {
                                subassemblyInsertionSamples = new List<long>();
                                elapsedMillisecondsByDurationType.Add(
                                    BusbarGenerationDurationType
                                        .SubassemblyInsertion,
                                    subassemblyInsertionSamples);
                            }

                            subassemblyInsertionSamples.Add(Math.Max(
                                1L,
                                subassemblyInsertionStopwatch
                                    .ElapsedMilliseconds));
                            GenerationLogger.Info(
                                "铜排子装配体保存插入完成：{0}，耗时={1}ms",
                                generationWorkGroup.TargetAssemblyBaseName,
                                subassemblyInsertionStopwatch
                                    .ElapsedMilliseconds);
                            var createdAssemblyPath =
                                createdAssemblyComponent?.GetPathName();
                            if (string.IsNullOrWhiteSpace(createdAssemblyPath))
                            {
                                throw new InvalidOperationException(
                                    "新建铜排子装配体没有返回有效文件路径。");
                            }

                            generationResult.CreatedPartPaths.AddRange(
                                generationAssemblyContext.CreatedPartPaths);
                            generationResult.CreatedAssemblyPaths.Add(
                                createdAssemblyPath);
                            GenerationLogger.Info(
                                "母排子装配体生成完成：{0}，路径={1}",
                                generationWorkGroup.TargetAssemblyBaseName,
                                createdAssemblyPath);
                            generationSubassemblyComplete = true;
                        }
                        catch (Exception subassemblyException)
                        {
                            if (!TryRollbackTemplateGenerationAssembly(
                                    activeAssemblyModel,
                                    activeAssemblyDocument,
                                    activeAssemblyPath,
                                    generationResult.OutputDirectoryPath,
                                    generationWorkGroup,
                                    generationAssemblyContext,
                                    out var rollbackFailureMessage))
                            {
                                throw new AlgorithmBusbarSubassemblyRollbackException(
                                    subassemblyException.Message +
                                    "；铜排子装配体回滚失败：" +
                                    rollbackFailureMessage,
                                    subassemblyException);
                            }

                            generationResult.Diagnostics.Add(
                                generationWorkGroup.DisplayName +
                                "：生成子装配体失败：" +
                                subassemblyException.Message);
                            GenerationLogger.Error(
                                subassemblyException,
                                "母排模板子装配体生成失败：{0}",
                                generationWorkGroup.DisplayName);
                        }
                        finally
                        {
                            completedGenerationStepCount++;
                            ReportGenerationToast(
                                generationProgressToast,
                                generationSubassemblyComplete
                                    ? "母排子装配体生成完成"
                                    : "母排子装配体生成失败",
                                completedGenerationStepCount,
                                totalGenerationStepCount,
                                generationWorkGroup.TargetAssemblyBaseName,
                                estimatedGenerationDurationMilliseconds);
                        }

                        if (!generationSubassemblyComplete)
                        {
                            break;
                        }
                    }

                    if (generationAttempted && _bridgeContext != null)
                    {
                        for (var numberIndex = 0; numberIndex <= highestConsumedDrawingNumberOffset; numberIndex++)
                            _bridgeContext.DrawingNumbers.CommitNext(_bridgeContext.DrawingNumbers.PeekNextDrawingNumber());
                    }
                    if (generationAttempted && _bridgeContext == null &&
                        !TryAdvanceAndPersistDrawingNumber(
                            drawingNumberDriver,
                            activeAssemblyModel,
                            drawingNumberMetadata,
                            highestConsumedDrawingNumberOffset + 1,
                            out var drawingNumberPersistenceMessage))
                    {
                        generationResult.Diagnostics.Add(
                            drawingNumberPersistenceMessage);
                    }

                    if (generationAttempted && _bridgeContext == null)
                    {
                        var assemblySaveErrors = 0;
                        var assemblySaveWarnings = 0;
                        var saved = activeAssemblyModel.Save3(
                                (int)(
                                    swSaveAsOptions_e.swSaveAsOptions_Silent |
                                    swSaveAsOptions_e
                                        .swSaveAsOptions_SaveReferenced),
                                ref assemblySaveErrors,
                                ref assemblySaveWarnings);
                        try
                        {
                            CheckGenerationSave(saved, assemblySaveErrors, assemblySaveWarnings,
                                "铜排生成结束保存总装：" + activeAssemblyModel.GetPathName());
                        }
                        catch (InvalidOperationException saveException)
                        {
                            GenerationLogger.Error(saveException, "铜排生成结束保存总装未通过校验。");
                            generationResult.Diagnostics.Add(saveException.Message);
                        }
                    }
                }
                finally
                {
                    BusbarGenerationDurationProfileStore
                        .SaveLastGenerationAverages(
                            elapsedMillisecondsByDurationType);
                    DisableAcceleratedGenerationMode();
                }

                return generationResult;
            });
        }

        /// <summary>
        /// 复用现有铜排材质库逻辑，只处理本次新建子装配体中的铜排零件。
        /// 在子装配体首次保存前执行，随后 SaveReferenced 会一并保存零件材质。
        /// </summary>
        private void ApplyGeneratedBusbarMaterials(
            ModelDoc2 generationAssemblyModel,
            string busbarMaterial,
            string generationGroupDisplayName)
        {
            var materialApplyResult = BusbarMaterialFailurePolicy.Execute(
                () => new BusbarDriver(_solidWorksApplication)
                    .ApplyBusbarMaterials(generationAssemblyModel, busbarMaterial, _preservedCopperPaths),
                busbarMaterial,
                showWarning: null);
            var materialApplySummary = materialApplyResult?
                .BuildSummaryMessage() ??
                "铜排材质设置没有返回结果。";

            if (materialApplyResult == null ||
                !materialApplyResult.IsDatabaseResolved ||
                !string.IsNullOrWhiteSpace(
                    materialApplyResult.ErrorMessage) ||
                materialApplyResult.FailedCount > 0 ||
                materialApplyResult.SuccessCount == 0)
            {
                if (_bridgeContext != null)
                    throw new InvalidOperationException("正式铜排材质设置失败：" + materialApplySummary);
                GenerationLogger.Warn("跳过新生成铜排材质设置，继续保存和装配：组={0}，原因={1}",
                    generationGroupDisplayName, materialApplySummary);
                return;
            }

            GenerationLogger.Info(
                "新生成铜排材质设置完成：组={0}，材质={1}，成功={2}，跳过={3}，材质库={4}",
                generationGroupDisplayName,
                materialApplyResult.MaterialName,
                materialApplyResult.SuccessCount,
                materialApplyResult.SkippedCount,
                materialApplyResult.DatabasePath);
        }

        /// <summary>
        /// 复用 CopperBarLinkage 自由铜排生成时的 SolidWorks 界面抑制设置，
        /// 避免批量打开模板零件时反复刷新窗口和最近文档列表。
        /// </summary>
        private void EnableAcceleratedGenerationMode()
        {
            _solidWorksApplication.SetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swLockRecentDocumentsList,
                true);
            _solidWorksApplication.DocumentVisible(
                false,
                (int)swDocumentTypes_e.swDocPART);
            _solidWorksApplication.EnableBackgroundProcessing = true;
            _solidWorksApplication.CommandInProgress = true;
        }

        private void DisableAcceleratedGenerationMode()
        {
            TryRestoreSolidWorksState(
                () => _solidWorksApplication.CommandInProgress = false,
                "恢复 SolidWorks 命令状态失败。");
            TryRestoreSolidWorksState(
                () => _solidWorksApplication.EnableBackgroundProcessing = false,
                "恢复 SolidWorks 后台处理状态失败。");
            TryRestoreSolidWorksState(
                () => _solidWorksApplication.DocumentVisible(
                    true,
                    (int)swDocumentTypes_e.swDocPART),
                "恢复 SolidWorks 零件文档显示失败。");
            TryRestoreSolidWorksState(
                () => _solidWorksApplication.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swLockRecentDocumentsList,
                    false),
                "恢复 SolidWorks 最近文档记录状态失败。");
        }

        private static void TryRestoreSolidWorksState(
            Action restoreAction,
            string failureMessage)
        {
            try
            {
                restoreAction();
            }
            catch (Exception restoreException)
            {
                GenerationLogger.Warn(
                    restoreException,
                    failureMessage);
            }
        }

        /// <summary>
        /// 按「加速生成」要求对铜排排序：普通直排/平弯/桥式直排靠前（可保持
        /// 图形刷新抑制加速），铣排、立弯靠后（生成前恢复图形刷新，因其依赖
        /// 实时图形与特征树刷新）。
        /// </summary>
        private static IReadOnlyList<AlgorithmBusbarGenerationItem>
            OrderGenerationItemsForAcceleratedGeneration(
            IReadOnlyList<AlgorithmBusbarGenerationItem> generationItems)
        {
            return generationItems
                .OrderBy(generationItem =>
                    RequiresGraphicsUpdate(generationItem) ? 1 : 0)
                .ToList();
        }

        /// <summary>
        /// 判断生成某铜排是否需要实时图形刷新。铣排、立弯模板生成依赖
        /// 实时图形与特征树刷新，抑制图形更新会导致其出错，返回 true。
        /// </summary>
        private static bool RequiresGraphicsUpdate(
            AlgorithmBusbarGenerationItem generationItem)
        {
            if (generationItem == null)
            {
                return false;
            }

            return generationItem.DurationType ==
                       BusbarGenerationDurationType.Milled ||
                   generationItem.DurationType ==
                       BusbarGenerationDurationType.VerticalBend;
        }

        /// <summary>
        /// 抑制当前模型视图的图形刷新（对齐 CopperBarLinkage 的
        /// ModelViewReset.OnlySaveConfig 思路）：关闭图形更新、等待光标、
        /// 特征树窗口，避免批量生成普通排时反复刷新界面拖慢速度。
        /// 幂等：已抑制时不再重复保存快照。
        /// </summary>
        private void SuspendModelViewGraphics()
        {
            if (_modelViewGraphicsSuspended)
            {
                return;
            }

            try
            {
                var document = _solidWorksApplication.IActiveDoc2;
                if (document == null)
                {
                    return;
                }

                var modelView = document.IActiveView as ModelView;
                if (modelView == null)
                {
                    return;
                }

                _modelViewSuppressedModelView = modelView;
                _modelViewSuppressedDocument = document;
                _modelViewOldSuppressWaitCursorDuringRedraw =
                    modelView.SuppressWaitCursorDuringRedraw;
                _modelViewOldEnableGraphicsUpdate =
                    modelView.EnableGraphicsUpdate;
                _modelViewOldEnableFeatureTreeWindow =
                    document.FeatureManager.EnableFeatureTreeWindow;
                _modelViewOldEnableFeatureTree =
                    document.FeatureManager.EnableFeatureTree;

                modelView.SuppressWaitCursorDuringRedraw = true;
                modelView.EnableGraphicsUpdate = false;
                document.FeatureManager.EnableFeatureTreeWindow = false;
                document.FeatureManager.EnableFeatureTree = false;
                _modelViewGraphicsSuspended = true;
            }
            catch (Exception suspendException)
            {
                GenerationLogger.Warn(
                    suspendException,
                    "抑制模型视图图形刷新失败。");
            }
        }

        /// <summary>
        /// 恢复被抑制的模型视图图形刷新。幂等，且每一步都用 try 保护，
        /// 确保即使部分恢复失败也不影响后续恢复。
        /// </summary>
        private void ResumeModelViewGraphics()
        {
            if (!_modelViewGraphicsSuspended)
            {
                return;
            }

            var modelView = _modelViewSuppressedModelView;
            var document = _modelViewSuppressedDocument;
            _modelViewGraphicsSuspended = false;
            _modelViewSuppressedModelView = null;
            _modelViewSuppressedDocument = null;

            if (document == null)
            {
                return;
            }

            TryRestoreSolidWorksState(
                () => document.FeatureManager.EnableFeatureTreeWindow =
                    _modelViewOldEnableFeatureTreeWindow,
                "恢复特征树窗口显示失败。");
            TryRestoreSolidWorksState(
                () => document.FeatureManager.EnableFeatureTree =
                    _modelViewOldEnableFeatureTree,
                "恢复特征树显示失败。");

            if (modelView == null)
            {
                return;
            }

            TryRestoreSolidWorksState(
                () => modelView.EnableGraphicsUpdate =
                    _modelViewOldEnableGraphicsUpdate,
                "恢复图形更新失败。");
            TryRestoreSolidWorksState(
                () => modelView.SuppressWaitCursorDuringRedraw =
                    _modelViewOldSuppressWaitCursorDuringRedraw,
                "恢复等待光标状态失败。");
            TryRestoreSolidWorksState(
                () => modelView.GraphicsRedraw(null),
                "强制刷新模型视图失败。");
        }

        private static List<AlgorithmBusbarGenerationWorkGroup>
            BuildGenerationWorkGroups(
            IReadOnlyList<AlgorithmBusbarGenerationGroup>
                sourceGenerationGroups)
        {
            var generationWorkGroups =
                new List<AlgorithmBusbarGenerationWorkGroup>();
            var unifiedGenerationWorkGroup =
                new AlgorithmBusbarGenerationWorkGroup
                {
                    DisplayName = "铜排总成"
                };
            for (var generationGroupIndex = 0;
                generationGroupIndex < sourceGenerationGroups.Count;
                generationGroupIndex++)
            {
                var sourceGenerationGroup =
                    sourceGenerationGroups[generationGroupIndex];
                var sourceGenerationGroupDisplayName =
                    string.IsNullOrWhiteSpace(sourceGenerationGroup.DisplayName)
                        ? string.Format(
                            CultureInfo.CurrentCulture,
                            "第{0}普通段组",
                            generationGroupIndex + 1)
                        : sourceGenerationGroup.DisplayName.Trim();
                var logicalSourceBusbars =
                    CreateLogicalGenerationBusbars(
                        sourceGenerationGroup,
                        sourceGenerationGroupDisplayName);

                for (var groupPartIndex = 0;
                    groupPartIndex < logicalSourceBusbars.Count;
                    groupPartIndex++)
                {
                    var sourceCenterlineBusbar =
                        logicalSourceBusbars[groupPartIndex].BusbarSetting;
                    if (sourceCenterlineBusbar == null)
                    {
                        throw new InvalidOperationException(string.Format(
                            CultureInfo.CurrentCulture,
                            "{0}第{1}根铜排数据为空。",
                            sourceGenerationGroupDisplayName,
                            groupPartIndex + 1));
                    }

                    var bendSetting = PlanarBendPreviewAdapter.Create(
                        sourceCenterlineBusbar);
                    if (bendSetting == null ||
                        bendSetting.PointGather == null ||
                        bendSetting.PointGather.Count < 2)
                    {
                        throw new InvalidOperationException(string.Format(
                            CultureInfo.CurrentCulture,
                            "{0}第{1}根铜排无法转换为自由折弯参数。",
                            sourceGenerationGroupDisplayName,
                            groupPartIndex + 1));
                    }

                    var verticalBendSetting = new BusbarGenerationIntegrity()
                        .ResolveVerticalBend(sourceCenterlineBusbar);
                    var shapePointGather = (verticalBendSetting != null
                            ? verticalBendSetting.PointList
                            : bendSetting.PointGather)
                        .Select(shapePoint => shapePoint.Copy())
                        .ToList()
                        .AsReadOnly();
                    var generationItem = new AlgorithmBusbarGenerationItem
                    {
                        BridgeKey = sourceGenerationGroupDisplayName +
                            (sourceGenerationGroups.Count(g => g.DisplayName == sourceGenerationGroup.DisplayName) > 1
                                ? sourceGenerationGroup.IsBridge ? ":bridge" : ":plain" : string.Empty) +
                            ":" + groupPartIndex.ToString(CultureInfo.InvariantCulture),
                        BridgeSetting = CKBusbarBridge.Prototype.BridgeNativePreviewCopy.Copy(sourceCenterlineBusbar),
                        BendSetting = bendSetting,
                        VerticalBendSetting = verticalBendSetting,
                        ShapePointGather = shapePointGather,
                        DurationType = ResolveGenerationDurationType(
                            sourceCenterlineBusbar,
                            sourceGenerationGroup.IsBridge,
                            bendSetting,
                            verticalBendSetting),
                        SourceGroupIndex = generationGroupIndex,
                        RestrictedSourceGroupIndex = sourceGenerationGroup
                                .RestrictSourceReuseToCurrentGroup
                            ? (int?)generationGroupIndex
                            : null,
                        TemplateName = ResolveGenerationTemplateName(
                            bendSetting,
                            verticalBendSetting,
                            logicalSourceBusbars[groupPartIndex]
                                .IsBridgeMilledBar)
                    };
                    foreach (var existingGenerationItem in
                        unifiedGenerationWorkGroup.GenerationItems)
                    {
                        if (generationItem.RestrictedSourceGroupIndex.HasValue &&
                            generationItem.RestrictedSourceGroupIndex !=
                            existingGenerationItem.SourceGroupIndex)
                        {
                            continue;
                        }

                        if (!TryMatchBusbarShape(
                                existingGenerationItem,
                                generationItem,
                                out var sourcePointOrderReversed))
                        {
                            continue;
                        }

                        generationItem.SourceGenerationItem =
                            existingGenerationItem;
                        generationItem.SourcePointOrderReversed =
                            sourcePointOrderReversed;
                        break;
                    }
                    unifiedGenerationWorkGroup.GenerationItems.Add(
                        generationItem);
                }
            }

            if (unifiedGenerationWorkGroup.GenerationItems.Select(item => item.BridgeKey)
                .Distinct(StringComparer.Ordinal).Count() != unifiedGenerationWorkGroup.GenerationItems.Count)
                throw new InvalidOperationException("铜排段组名称重复，无法建立唯一的二次驱动零件映射。");
            generationWorkGroups.Add(unifiedGenerationWorkGroup);
            return generationWorkGroups;
        }

        private static IReadOnlyList<LogicalGenerationBusbar>
            CreateLogicalGenerationBusbars(
            AlgorithmBusbarGenerationGroup sourceGenerationGroup,
            string sourceGenerationGroupDisplayName)
        {
            var logicalGenerationBusbars =
                new List<LogicalGenerationBusbar>();
            var consumedMilledBarGroupKeys = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (var sourceBusbarSetting in sourceGenerationGroup.Busbars)
            {
                if (sourceBusbarSetting == null ||
                    !sourceBusbarSetting.IsMilledBar)
                {
                    logicalGenerationBusbars.Add(new LogicalGenerationBusbar
                    {
                        BusbarSetting = sourceBusbarSetting
                    });
                    continue;
                }

                var milledBarGroupKey = sourceBusbarSetting.GroupKey ??
                    string.Empty;
                if (string.IsNullOrWhiteSpace(milledBarGroupKey))
                {
                    throw new InvalidOperationException(
                        sourceGenerationGroupDisplayName +
                        "包含缺少 GroupKey 的铣排预览段。");
                }
                if (!consumedMilledBarGroupKeys.Add(milledBarGroupKey))
                {
                    continue;
                }

                var milledBarSegments = sourceGenerationGroup.Busbars
                    .Where(candidateBusbarSetting =>
                        candidateBusbarSetting?.IsMilledBar == true &&
                        string.Equals(
                            candidateBusbarSetting.GroupKey,
                            milledBarGroupKey,
                            StringComparison.Ordinal))
                    .ToList();
                logicalGenerationBusbars.Add(CreateLogicalMilledBusbar(
                    milledBarSegments,
                    sourceGenerationGroupDisplayName,
                    milledBarGroupKey));
            }

            return logicalGenerationBusbars.AsReadOnly();
        }

        private static LogicalGenerationBusbar CreateLogicalMilledBusbar(
            IReadOnlyList<PlanarBendBusbarSetting> milledBarSegments,
            string sourceGenerationGroupDisplayName,
            string milledBarGroupKey)
        {
            if (milledBarSegments == null || milledBarSegments.Count != 3 ||
                milledBarSegments.Any(milledBarSegment =>
                    milledBarSegment?.PointList == null ||
                    milledBarSegment.PointList.Count < 2))
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}中的铣排“{1}”必须包含三段有效预览线。",
                    sourceGenerationGroupDisplayName,
                    milledBarGroupKey));
            }

            var firstMilledBarSegment = milledBarSegments[0];
            var middleMilledBarSegment = milledBarSegments[1];
            var lastMilledBarSegment = milledBarSegments[2];
            var originalBusbarWidth = firstMilledBarSegment
                .MilledBarOriginalWidth > DirectionTolerance
                    ? firstMilledBarSegment.MilledBarOriginalWidth
                    : Math.Min(
                        firstMilledBarSegment.BusBarWidth,
                        lastMilledBarSegment.BusBarWidth);
            var additionalBusbarWidth = firstMilledBarSegment
                .MilledBarAdditionalWidth > DirectionTolerance
                    ? firstMilledBarSegment.MilledBarAdditionalWidth
                    : Math.Max(
                        0d,
                        middleMilledBarSegment.BusBarWidth -
                        originalBusbarWidth);
            if (originalBusbarWidth <= DirectionTolerance ||
                additionalBusbarWidth <= DirectionTolerance)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}中的铣排“{1}”缺少有效的排宽或增加宽。",
                    sourceGenerationGroupDisplayName,
                    milledBarGroupKey));
            }

            var widthDirection = firstMilledBarSegment.WidthVector;
            if (widthDirection == null ||
                widthDirection.Norm <= DirectionTolerance)
            {
                throw new InvalidOperationException(
                    "铣排宽度方向无效：" + milledBarGroupKey + "。");
            }
            widthDirection = widthDirection.Normalized;
            var firstInnerPoint = firstMilledBarSegment.PointList[
                firstMilledBarSegment.PointList.Count - 1];
            var lastInnerPoint = lastMilledBarSegment.PointList[0];
            var signedCenterOffset = new Vector3d(
                firstInnerPoint,
                lastInnerPoint).Dot(widthDirection);
            if (Math.Abs(
                    Math.Abs(signedCenterOffset) - additionalBusbarWidth) >
                SameSourcePointToleranceMillimeters)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.CurrentCulture,
                    "铣排“{0}”两端中心偏移 {1:0.###}mm 与增加宽 {2:0.###}mm 不一致。",
                    milledBarGroupKey,
                    Math.Abs(signedCenterOffset),
                    additionalBusbarWidth));
            }

            var firstCenterShift = signedCenterOffset / 2d;
            var lastCenterShift = -signedCenterOffset / 2d;
            var simulatedWideBarCenterPoints = firstMilledBarSegment.PointList
                .Select(sourcePoint => TranslatePoint(
                    sourcePoint,
                    widthDirection,
                    firstCenterShift))
                .Concat(lastMilledBarSegment.PointList.Select(sourcePoint =>
                    TranslatePoint(
                        sourcePoint,
                        widthDirection,
                        lastCenterShift)))
                .ToList();
            var isBridgeMilledBar =
                firstMilledBarSegment.PointList.Count > 2 ||
                lastMilledBarSegment.PointList.Count > 2;
            if (isBridgeMilledBar)
            {
                // 桥式铣排模板以五条草图线逐点驱动；铣削区边界即使共线，
                // 也必须作为两处独立结构点保留。
                if (simulatedWideBarCenterPoints.Count != 6 ||
                    Enumerable.Range(1, simulatedWideBarCenterPoints.Count - 1)
                        .Any(index => simulatedWideBarCenterPoints[index - 1]
                            .DistanceTo(simulatedWideBarCenterPoints[index]) <= DirectionTolerance))
                {
                    throw new InvalidOperationException(
                        "桥式铣排“" + milledBarGroupKey + "”必须具有六个连续且不重合的正式路径点。");
                }
            }
            else
            {
                simulatedWideBarCenterPoints =
                    RemoveCollinearCenterlinePoints(simulatedWideBarCenterPoints);
            }

            var logicalMilledBarSetting = firstMilledBarSegment.Copy();
            logicalMilledBarSetting.PointList = simulatedWideBarCenterPoints;
            if (isBridgeMilledBar)
                logicalMilledBarSetting.PreviewBendPointKinds = Enumerable.Repeat(
                    BusbarPreviewBendPointKind_e.Planar, simulatedWideBarCenterPoints.Count).ToList();
            logicalMilledBarSetting.BusBarWidth =
                originalBusbarWidth + additionalBusbarWidth;
            logicalMilledBarSetting.StartLapWidth = originalBusbarWidth;
            logicalMilledBarSetting.EndLapWidth = originalBusbarWidth;
            logicalMilledBarSetting.IsMilledBar = true;
            logicalMilledBarSetting.MilledBarOriginalWidth =
                originalBusbarWidth;
            logicalMilledBarSetting.MilledBarAdditionalWidth =
                additionalBusbarWidth;
            logicalMilledBarSetting.MilledBarStartLength =
                firstMilledBarSegment.MilledBarStartLength >
                    DirectionTolerance
                    ? firstMilledBarSegment.MilledBarStartLength
                    : firstMilledBarSegment.GetDevelopedLength();
            logicalMilledBarSetting.MilledBarEndLength =
                lastMilledBarSegment.MilledBarEndLength >
                    DirectionTolerance
                    ? lastMilledBarSegment.MilledBarEndLength
                    : lastMilledBarSegment.GetDevelopedLength();
            logicalMilledBarSetting.MilledBarFeatureSide =
                ResolveMilledBarFeatureSide(
                    logicalMilledBarSetting,
                    signedCenterOffset);

            GenerationLogger.Info(
                "铣排正式生成数据已合并：GroupKey={0}，类型={1}，排宽W={2:0.###}mm，增加宽={3:0.###}mm，总排宽={4:0.###}mm，起端铣削位置={5:0.###}mm，末端铣削位置={6:0.###}mm，方向={7}，中心点数={8}",
                milledBarGroupKey,
                isBridgeMilledBar ? "桥式铣排" : "直排铣排",
                originalBusbarWidth,
                additionalBusbarWidth,
                logicalMilledBarSetting.BusBarWidth,
                logicalMilledBarSetting.MilledBarStartLength,
                logicalMilledBarSetting.MilledBarEndLength,
                logicalMilledBarSetting.MilledBarFeatureSide,
                logicalMilledBarSetting.PointList.Count);
            return new LogicalGenerationBusbar
            {
                BusbarSetting = logicalMilledBarSetting,
                IsBridgeMilledBar = isBridgeMilledBar
            };
        }

        private static MilledBarFeatureSide_e ResolveMilledBarFeatureSide(
            PlanarBendBusbarSetting logicalMilledBarSetting,
            double signedCenterOffset)
        {
            var firstPathDirection = new Vector3d(
                logicalMilledBarSetting.PointList[0],
                logicalMilledBarSetting.PointList[1]).Normalized;
            var sourceWidthDirection =
                logicalMilledBarSetting.WidthVector.Normalized;
            var calculatedThicknessDirection = firstPathDirection.Cross(
                sourceWidthDirection);
            if (calculatedThicknessDirection.Norm <= DirectionTolerance ||
                logicalMilledBarSetting.StartBusbarVector == null ||
                logicalMilledBarSetting.StartBusbarVector.Norm <=
                    DirectionTolerance)
            {
                throw new InvalidOperationException(
                    "铣排无法根据路径方向确定左右特征。");
            }

            // 与 SetBusbarTransform 使用相同的普通铜排模板方向约定，先
            // 把装配体宽向偏移换算到零件局部 Y 方向，再决定左右特征。
            var templateWidthDirectionReversed =
                calculatedThicknessDirection.Normalized.Dot(
                    logicalMilledBarSetting.StartBusbarVector.Normalized) > 0d;
            var signedTemplateWidthOffset = templateWidthDirectionReversed
                ? -signedCenterOffset
                : signedCenterOffset;
            return signedTemplateWidthOffset < 0d
                ? MilledBarFeatureSide_e.Left
                : MilledBarFeatureSide_e.Right;
        }

        private static Point3d TranslatePoint(
            Point3d sourcePoint,
            Vector3d translationDirection,
            double translationDistance)
        {
            return new Point3d(
                sourcePoint.X + translationDirection.X * translationDistance,
                sourcePoint.Y + translationDirection.Y * translationDistance,
                sourcePoint.Z + translationDirection.Z * translationDistance);
        }

        private static List<Point3d> RemoveCollinearCenterlinePoints(
            IReadOnlyList<Point3d> sourceCenterlinePoints)
        {
            var simplifiedCenterlinePoints = new List<Point3d>();
            foreach (var sourceCenterlinePoint in sourceCenterlinePoints)
            {
                if (simplifiedCenterlinePoints.Count > 0 &&
                    simplifiedCenterlinePoints[
                        simplifiedCenterlinePoints.Count - 1]
                        .DistanceTo(sourceCenterlinePoint) <= DirectionTolerance)
                {
                    continue;
                }

                simplifiedCenterlinePoints.Add(sourceCenterlinePoint.Copy());
                while (simplifiedCenterlinePoints.Count >= 3)
                {
                    var lastPointIndex =
                        simplifiedCenterlinePoints.Count - 1;
                    var precedingDirection = new Vector3d(
                        simplifiedCenterlinePoints[lastPointIndex - 2],
                        simplifiedCenterlinePoints[lastPointIndex - 1]);
                    var followingDirection = new Vector3d(
                        simplifiedCenterlinePoints[lastPointIndex - 1],
                        simplifiedCenterlinePoints[lastPointIndex]);
                    if (precedingDirection.Norm <= DirectionTolerance ||
                        followingDirection.Norm <= DirectionTolerance ||
                        precedingDirection.Cross(followingDirection).Norm >
                            DirectionTolerance * precedingDirection.Norm *
                            followingDirection.Norm ||
                        precedingDirection.Dot(followingDirection) <= 0d)
                    {
                        break;
                    }

                    simplifiedCenterlinePoints.RemoveAt(lastPointIndex - 1);
                }
            }

            return simplifiedCenterlinePoints;
        }

        private static string ResolveGenerationTemplateName(
            BendPreviewSetting bendSetting,
            VerticalBendSetting verticalBendSetting,
            bool isBridgeMilledBar)
        {
            if (verticalBendSetting != null)
            {
                return VerticalBendTemplateName;
            }
            if (bendSetting.IsMilledBar)
            {
                return isBridgeMilledBar
                    ? BridgeMilledBusbarTemplateName
                    : StraightMilledBusbarTemplateName;
            }

            return bendSetting.PointGather.Count == 2
                ? StraightBusbarTemplateName
                : PlanarBendTemplateName;
        }

        private static BusbarGenerationDurationType
            ResolveGenerationDurationType(
            PlanarBendBusbarSetting sourceCenterlineBusbar,
            bool sourceGroupIsBridge,
            BendPreviewSetting bendSetting,
            VerticalBendSetting verticalBendSetting)
        {
            if (verticalBendSetting != null ||
                sourceCenterlineBusbar.IsVerticalBend)
            {
                return BusbarGenerationDurationType.VerticalBend;
            }

            if (sourceCenterlineBusbar.IsMilledBar || bendSetting.IsMilledBar)
            {
                return BusbarGenerationDurationType.Milled;
            }

            if (sourceGroupIsBridge ||
                IsBridgeCenterline(sourceCenterlineBusbar.PointList))
            {
                return BusbarGenerationDurationType.BridgeStraight;
            }

            return bendSetting.PointGather.Count > 2
                ? BusbarGenerationDurationType.PlanarBend
                : BusbarGenerationDurationType.OrdinaryStraight;
        }

        private static bool IsBridgeCenterline(
            IReadOnlyList<Point3d> centerlinePoints)
        {
            if (centerlinePoints == null || centerlinePoints.Count != 6)
            {
                return false;
            }

            var firstStraightDirection =
                new Vector3d(centerlinePoints[0], centerlinePoints[1]);
            var middleStraightDirection =
                new Vector3d(centerlinePoints[2], centerlinePoints[3]);
            var lastStraightDirection =
                new Vector3d(centerlinePoints[4], centerlinePoints[5]);
            return AreParallelDirections(
                       firstStraightDirection,
                       middleStraightDirection) &&
                   AreParallelDirections(
                       firstStraightDirection,
                       lastStraightDirection);
        }

        private static bool AreParallelDirections(
            Vector3d firstDirection,
            Vector3d secondDirection)
        {
            if (firstDirection == null || secondDirection == null ||
                firstDirection.Norm <= DirectionTolerance ||
                secondDirection.Norm <= DirectionTolerance)
            {
                return false;
            }

            return firstDirection.Cross(secondDirection).Norm <=
                   DirectionTolerance *
                   firstDirection.Norm * secondDirection.Norm;
        }

        private static bool TryMatchBusbarShape(
            AlgorithmBusbarGenerationItem firstGenerationItem,
            AlgorithmBusbarGenerationItem secondGenerationItem,
            out bool secondPointOrderReversed)
        {
            secondPointOrderReversed = false;
            if (firstGenerationItem == null ||
                secondGenerationItem == null ||
                !string.Equals(
                    firstGenerationItem.TemplateName,
                    secondGenerationItem.TemplateName,
                    StringComparison.OrdinalIgnoreCase) ||
                !HaveSameBusbarPhysicalParameters(
                    firstGenerationItem.BendSetting,
                    secondGenerationItem.BendSetting))
            {
                return false;
            }

            if (HaveSamePointDistanceMatrix(
                    firstGenerationItem.ShapePointGather,
                    secondGenerationItem.ShapePointGather,
                    false) &&
                HaveSameMilledBarEndSections(
                    firstGenerationItem.BendSetting,
                    secondGenerationItem.BendSetting,
                    false))
            {
                return true;
            }

            if (HaveSamePointDistanceMatrix(
                    firstGenerationItem.ShapePointGather,
                    secondGenerationItem.ShapePointGather,
                    true) &&
                HaveSameMilledBarEndSections(
                    firstGenerationItem.BendSetting,
                    secondGenerationItem.BendSetting,
                    true))
            {
                secondPointOrderReversed = true;
                return true;
            }

            return false;
        }

        private static bool HaveSameBusbarPhysicalParameters(
            BendPreviewSetting firstBendSetting,
            BendPreviewSetting secondBendSetting)
        {
            if (firstBendSetting == null || secondBendSetting == null)
            {
                return false;
            }

            return Math.Abs(
                    firstBendSetting.BusBarWidth -
                    secondBendSetting.BusBarWidth) <=
                        SameSourcePointToleranceMillimeters &&
                Math.Abs(
                    firstBendSetting.BusBarThickness -
                    secondBendSetting.BusBarThickness) <=
                        SameSourcePointToleranceMillimeters &&
                Math.Abs(
                    firstBendSetting.BarBendingRadius -
                    secondBendSetting.BarBendingRadius) <=
                        SameSourcePointToleranceMillimeters &&
                Math.Abs(
                    firstBendSetting.BusBarVerticalRadius -
                    secondBendSetting.BusBarVerticalRadius) <=
                        SameSourcePointToleranceMillimeters &&
                firstBendSetting.IsMilledBar ==
                    secondBendSetting.IsMilledBar &&
                Math.Abs(
                    firstBendSetting.MilledBarOriginalWidth -
                    secondBendSetting.MilledBarOriginalWidth) <=
                        SameSourcePointToleranceMillimeters &&
                Math.Abs(
                    firstBendSetting.MilledBarAdditionalWidth -
                    secondBendSetting.MilledBarAdditionalWidth) <=
                        SameSourcePointToleranceMillimeters &&
                firstBendSetting.MilledBarFeatureSide ==
                    secondBendSetting.MilledBarFeatureSide;
        }

        private static bool HaveSameMilledBarEndSections(
            BendPreviewSetting firstBendSetting,
            BendPreviewSetting secondBendSetting,
            bool compareSecondBendSettingInReverse)
        {
            if (!firstBendSetting.IsMilledBar)
            {
                return true;
            }

            var expectedSecondStartLength = compareSecondBendSettingInReverse
                ? secondBendSetting.MilledBarEndLength
                : secondBendSetting.MilledBarStartLength;
            var expectedSecondEndLength = compareSecondBendSettingInReverse
                ? secondBendSetting.MilledBarStartLength
                : secondBendSetting.MilledBarEndLength;
            return Math.Abs(
                       firstBendSetting.MilledBarStartLength -
                       expectedSecondStartLength) <=
                   SameSourcePointToleranceMillimeters &&
                   Math.Abs(
                       firstBendSetting.MilledBarEndLength -
                       expectedSecondEndLength) <=
                   SameSourcePointToleranceMillimeters;
        }

        private static bool HaveSamePointDistanceMatrix(
            IReadOnlyList<Point3d> firstPointGather,
            IReadOnlyList<Point3d> secondPointGather,
            bool compareSecondPointGatherInReverse)
        {
            if (firstPointGather == null ||
                secondPointGather == null ||
                firstPointGather.Count != secondPointGather.Count)
            {
                return false;
            }

            for (var firstPointIndex = 0;
                firstPointIndex < firstPointGather.Count;
                firstPointIndex++)
            {
                if (firstPointGather[firstPointIndex] == null)
                {
                    return false;
                }

                var secondPointIndex = compareSecondPointGatherInReverse
                    ? secondPointGather.Count - 1 - firstPointIndex
                    : firstPointIndex;
                if (secondPointGather[secondPointIndex] == null)
                {
                    return false;
                }

                for (var firstComparisonPointIndex = firstPointIndex + 1;
                    firstComparisonPointIndex < firstPointGather.Count;
                    firstComparisonPointIndex++)
                {
                    var secondComparisonPointIndex =
                        compareSecondPointGatherInReverse
                            ? secondPointGather.Count - 1 -
                                firstComparisonPointIndex
                            : firstComparisonPointIndex;
                    if (firstPointGather[firstComparisonPointIndex] == null ||
                        secondPointGather[secondComparisonPointIndex] == null)
                    {
                        return false;
                    }

                    var firstPointDistance = firstPointGather[firstPointIndex]
                        .DistanceTo(
                            firstPointGather[firstComparisonPointIndex]);
                    var secondPointDistance = secondPointGather[secondPointIndex]
                        .DistanceTo(
                            secondPointGather[secondComparisonPointIndex]);
                    if (Math.Abs(firstPointDistance - secondPointDistance) >
                        SameSourcePointToleranceMillimeters)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void AssignAvailableGenerationOutputNames(
            IEnumerable<AlgorithmBusbarGenerationWorkGroup>
                generationWorkGroups,
            string startingDrawingNumber,
            string outputDirectoryPath,
            AssemblyDoc parentAssemblyDocument)
        {
            var orderedGenerationWorkGroups =
                (generationWorkGroups ??
                    Enumerable.Empty<AlgorithmBusbarGenerationWorkGroup>())
                .ToList();
            if (!DrawingNumberHelper.TryParse(
                    startingDrawingNumber,
                    out _,
                    out var startingSerialNumber))
            {
                throw new FormatException(
                    "起始图号格式无效，无法自动避让已有母排文件：" +
                    startingDrawingNumber);
            }

            var unavailableOutputPaths = new HashSet<string>(
                CaptureReferencedComponentPaths(parentAssemblyDocument),
                StringComparer.OrdinalIgnoreCase);
            // 6/8 前缀分别是子装配体和零件的独立编号空间；
            // 两者均从总装当前图号起步，各自跳过已有文件和设计树引用。
            var nextAssemblyDrawingNumberOffset = 0;
            foreach (var generationWorkGroup in
                orderedGenerationWorkGroups)
            {
                AssignNextAvailableOutputName(
                    startingDrawingNumber,
                    startingSerialNumber,
                    outputDirectoryPath,
                    ".SLDASM",
                    DrawingNumberHelper.ToBusbarAssemblyFileName,
                    unavailableOutputPaths,
                    ref nextAssemblyDrawingNumberOffset,
                    out var availableAssemblyDrawingNumber,
                    out var availableAssemblyDrawingNumberOffset,
                    out var availableAssemblyBaseName);
                generationWorkGroup.DrawingNumber =
                    availableAssemblyDrawingNumber;
                generationWorkGroup.DrawingNumberOffset =
                    availableAssemblyDrawingNumberOffset;
                generationWorkGroup.TargetAssemblyBaseName =
                    availableAssemblyBaseName;
            }

            var nextPartDrawingNumberOffset = 0;
            foreach (var generationItem in orderedGenerationWorkGroups
                .SelectMany(generationWorkGroup =>
                    generationWorkGroup.GenerationItems))
            {
                if (generationItem.SourceGenerationItem != null)
                {
                    generationItem.DrawingNumber =
                        generationItem.SourceGenerationItem.DrawingNumber;
                    generationItem.DrawingNumberOffset =
                        generationItem.SourceGenerationItem.DrawingNumberOffset;
                    generationItem.TargetPartBaseName =
                        generationItem.SourceGenerationItem.TargetPartBaseName;
                    continue;
                }

                AssignNextAvailableOutputName(
                    startingDrawingNumber,
                    startingSerialNumber,
                    outputDirectoryPath,
                    ".SLDPRT",
                    DrawingNumberHelper.ToBusbarPartFileName,
                    unavailableOutputPaths,
                    ref nextPartDrawingNumberOffset,
                    out var availablePartDrawingNumber,
                    out var availablePartDrawingNumberOffset,
                    out var availablePartBaseName);
                generationItem.DrawingNumber = availablePartDrawingNumber;
                generationItem.DrawingNumberOffset =
                    availablePartDrawingNumberOffset;
                generationItem.TargetPartBaseName = availablePartBaseName;
            }
        }

        private static void AssignNextAvailableOutputName(
            string startingDrawingNumber,
            int startingSerialNumber,
            string outputDirectoryPath,
            string targetFileExtension,
            Func<string, string> outputBaseNameFactory,
            ISet<string> unavailableOutputPaths,
            ref int nextDrawingNumberOffset,
            out string availableDrawingNumber,
            out int availableDrawingNumberOffset,
            out string availableOutputBaseName)
        {
            while (startingSerialNumber + nextDrawingNumberOffset <= 9999)
            {
                var candidateDrawingNumberOffset =
                    nextDrawingNumberOffset;
                nextDrawingNumberOffset++;
                var candidateDrawingNumber = DrawingNumberHelper.Offset(
                    startingDrawingNumber,
                    candidateDrawingNumberOffset);
                var candidateOutputBaseName = outputBaseNameFactory(
                    candidateDrawingNumber);
                var candidateOutputPath = Path.GetFullPath(Path.Combine(
                    outputDirectoryPath,
                    candidateOutputBaseName + targetFileExtension));
                var candidatePathAlreadyReserved =
                    !unavailableOutputPaths.Add(candidateOutputPath);
                var candidatePathExists =
                    File.Exists(candidateOutputPath) ||
                    Directory.Exists(candidateOutputPath);
                if (!candidatePathAlreadyReserved && !candidatePathExists)
                {
                    availableDrawingNumber = candidateDrawingNumber;
                    availableDrawingNumberOffset =
                        candidateDrawingNumberOffset;
                    availableOutputBaseName = candidateOutputBaseName;
                    return;
                }

                GenerationLogger.Info(
                    "母排输出名称已被占用，自动递增四位流水号：图号={0}，路径={1}",
                    candidateDrawingNumber,
                    candidateOutputPath);
            }

            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture,
                "母排输出名称无可用四位流水号：起始图号={0}，文件类型={1}。",
                startingDrawingNumber,
                targetFileExtension));
        }

        private static void ValidateGenerationOutputPaths(
            IEnumerable<AlgorithmBusbarGenerationWorkGroup>
                generationWorkGroups,
            string outputDirectoryPath,
            AssemblyDoc parentAssemblyDocument)
        {
            var plannedOutputPaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var referencedComponentPaths =
                CaptureReferencedComponentPaths(parentAssemblyDocument);
            foreach (var generationWorkGroup in generationWorkGroups ??
                Enumerable.Empty<AlgorithmBusbarGenerationWorkGroup>())
            {
                var targetAssemblyPath = Path.Combine(
                    outputDirectoryPath,
                    generationWorkGroup.TargetAssemblyBaseName + ".SLDASM");
                ValidateGenerationOutputPath(
                    plannedOutputPaths,
                    referencedComponentPaths,
                    targetAssemblyPath,
                    "铜排子装配体");

                foreach (var generationItem in
                    generationWorkGroup.GenerationItems)
                {
                    if (generationItem.SourceGenerationItem != null)
                    {
                        continue;
                    }

                    var targetPartPath = Path.Combine(
                        outputDirectoryPath,
                        generationItem.TargetPartBaseName + ".SLDPRT");
                    ValidateGenerationOutputPath(
                        plannedOutputPaths,
                        referencedComponentPaths,
                        targetPartPath,
                        "铜排零件");
                }
            }
        }

        private static void ValidateGenerationOutputPath(
            ISet<string> plannedOutputPaths,
            ISet<string> referencedComponentPaths,
            string targetOutputPath,
            string outputKind)
        {
            var normalizedTargetOutputPath = Path.GetFullPath(
                targetOutputPath);
            if (!plannedOutputPaths.Add(normalizedTargetOutputPath))
            {
                throw new InvalidOperationException(
                    outputKind + "命名重复：" +
                    normalizedTargetOutputPath);
            }

            if (File.Exists(normalizedTargetOutputPath))
            {
                throw new IOException(
                    outputKind + "目标文件已存在，请先处理已有文件：" +
                    normalizedTargetOutputPath);
            }

            if (referencedComponentPaths.Contains(
                    normalizedTargetOutputPath))
            {
                throw new InvalidOperationException(
                    outputKind +
                    "目标路径已被当前总装配体设计树引用，请先处理现有组件：" +
                    normalizedTargetOutputPath);
            }
        }

        private static ISet<string> CaptureReferencedComponentPaths(
            AssemblyDoc parentAssemblyDocument)
        {
            var referencedComponentPaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var referencedComponent in
                (parentAssemblyDocument?.GetComponents(false) as object[] ??
                    Array.Empty<object>()).OfType<Component2>())
            {
                try
                {
                    var referencedComponentPath =
                        referencedComponent.GetPathName();
                    if (!string.IsNullOrWhiteSpace(referencedComponentPath))
                    {
                        referencedComponentPaths.Add(
                            Path.GetFullPath(referencedComponentPath));
                    }
                }
                catch (Exception componentPathException)
                {
                    GenerationLogger.Warn(
                        componentPathException,
                        "读取总装配体现有组件路径失败，已跳过该组件。");
                }
            }

            return referencedComponentPaths;
        }

        private static void ReportGenerationToast(
            IProgress<CKBusbarBridgePreview.FeedbackProgress> generationProgressToast,
            string generationStage,
            int completedGenerationStepCount,
            int totalGenerationStepCount,
            string generationDetail,
            long estimatedGenerationDurationMilliseconds)
        {
            generationProgressToast?.Report(
                new CKBusbarBridgePreview.FeedbackProgress
                {
                    Stage = generationStage,
                    Current = completedGenerationStepCount,
                    Total = totalGenerationStepCount,
                    Detail = generationDetail,
                    EstimatedDurationMilliseconds =
                        estimatedGenerationDurationMilliseconds > 0L
                            ? estimatedGenerationDurationMilliseconds
                            : (long?)null
                });
        }

        private static bool TryAdvanceAndPersistDrawingNumber(
            SolidWorksDriver drawingNumberDriver,
            ModelDoc2 activeAssemblyModel,
            DrawingNumberMetadataSnapshot drawingNumberMetadata,
            int consumedDrawingNumberCount,
            out string failureMessage)
        {
            failureMessage = null;
            if (consumedDrawingNumberCount <= 0)
            {
                return true;
            }

            try
            {
                var drawingNumberGenerator = new DrawingNumberGenerator(
                    drawingNumberMetadata.DrawingNumber);
                for (var consumedDrawingNumberIndex = 0;
                    consumedDrawingNumberIndex < consumedDrawingNumberCount;
                    consumedDrawingNumberIndex++)
                {
                    var consumedDrawingNumber =
                        drawingNumberGenerator.PeekNextDrawingNumber();
                    drawingNumberGenerator.CommitNext(consumedDrawingNumber);
                }

                if (DrawingNumberMetadataHelper
                    .TryPersistNextUnusedDrawingNumber(
                        drawingNumberDriver,
                        activeAssemblyModel,
                        drawingNumberMetadata,
                        drawingNumberGenerator,
                        out _))
                {
                    return true;
                }

                failureMessage =
                    "母排已经生成，但四位流水号已用尽，无法写回下一未用图号。";
                return false;
            }
            catch (Exception drawingNumberPersistenceException)
            {
                failureMessage =
                    "母排已经生成，但写回下一未用图号失败：" +
                    drawingNumberPersistenceException.Message;
                GenerationLogger.Error(
                    drawingNumberPersistenceException,
                    "母排生成后推进图号流水号失败。");
                return false;
            }
        }

        private void CreateTemplateGenerationAssembly(
            string outputDirectoryPath,
            string templateDirectory,
            AlgorithmBusbarGenerationWorkGroup generationWorkGroup,
            DrawingNumberMetadataSnapshot drawingNumberMetadata,
            TemplateGenerationAssemblyContext generationAssemblyContext)
        {
            if (generationAssemblyContext == null)
            {
                throw new ArgumentNullException(
                    nameof(generationAssemblyContext));
            }

            var sourceAssemblyTemplatePath = Path.Combine(
                templateDirectory,
                GenerationAssemblyTemplateName + ".SLDASM");
            var targetAssemblyPath = Path.Combine(
                outputDirectoryPath,
                generationWorkGroup.TargetAssemblyBaseName + ".SLDASM");
            generationAssemblyContext.AssemblyPath = targetAssemblyPath;
            if (File.Exists(targetAssemblyPath))
            {
                throw new IOException(
                    "目标普通段子装配体已存在：" +
                    targetAssemblyPath);
            }

            File.Copy(
                sourceAssemblyTemplatePath,
                targetAssemblyPath,
                false);
            generationAssemblyContext.OwnedOutputPaths.Add(
                targetAssemblyPath);
            var assemblyOpenErrors = 0;
            var assemblyOpenWarnings = 0;
            var generationAssemblyModel =
                _solidWorksApplication.OpenDoc6(
                    targetAssemblyPath,
                    (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    string.Empty,
                    ref assemblyOpenErrors,
                    ref assemblyOpenWarnings) as ModelDoc2;
            var generationAssemblyDocument =
                generationAssemblyModel as AssemblyDoc;
            if (generationAssemblyModel == null ||
                generationAssemblyDocument == null ||
                generationAssemblyModel.GetType() !=
                    (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.CurrentCulture,
                    "无法打开复制后的普通段子装配体模板，错误码：{0}，警告码：{1}。",
                    assemblyOpenErrors,
                    assemblyOpenWarnings));
            }

            ActivateExpectedAssemblyDocument(
                targetAssemblyPath,
                "激活复制后的普通段子装配体");
            WriteDrawingMetadata(
                generationAssemblyModel,
                generationWorkGroup.TargetAssemblyBaseName,
                drawingNumberMetadata);
            WriteCustomProperty(
                generationAssemblyModel,
                string.Empty,
                GeneratedBusbarSubassemblyIdentity.RdTypePropertyName,
                GeneratedBusbarSubassemblyIdentity.RdTypePropertyValue);
            RebuildGeneratedModel(generationAssemblyModel, "创建铜排子装配体");
            GenerationLogger.Info(
                "已从模板创建普通段子装配体：{0}，模板={1}",
                targetAssemblyPath,
                sourceAssemblyTemplatePath);
            generationAssemblyContext.AssemblyTitle =
                generationAssemblyModel.GetTitle();
            generationAssemblyContext.AssemblyModel =
                generationAssemblyModel;
            generationAssemblyContext.AssemblyDocument =
                generationAssemblyDocument;
        }

        private Component2 SaveAndInsertTemplateGenerationAssembly(
            ModelDoc2 parentAssemblyModel,
            AssemblyDoc parentAssemblyDocument,
            string parentAssemblyPath,
            TemplateGenerationAssemblyContext generationAssemblyContext,
            AlgorithmBusbarGenerationWorkGroup generationWorkGroup)
        {
            if (generationAssemblyContext == null ||
                generationAssemblyContext.AssemblyModel == null ||
                generationAssemblyContext.AssemblyDocument == null)
            {
                throw new InvalidOperationException(
                    "普通段子装配体文档无效。");
            }

            if (generationAssemblyContext.CreatedPartPaths.Count !=
                generationWorkGroup.GenerationItems.Count)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.CurrentCulture,
                    "普通段子装配体内铜排数量不完整：应生成 {0} 根，实际生成 {1} 根。",
                    generationWorkGroup.GenerationItems.Count,
                    generationAssemblyContext.CreatedPartPaths.Count));
            }

            foreach (var component in (generationAssemblyContext.AssemblyDocument.GetComponents(true) as object[] ?? Array.Empty<object>())
                .OfType<Component2>().Where(c => !c.IsSuppressed()))
                RequireCopperComponent(component, "铜排子装配体保存前");
            RebuildGeneratedModel(generationAssemblyContext.AssemblyModel, "保存铜排子装配体");
            SaveAssemblyWithReferences(
                generationAssemblyContext.AssemblyModel,
                generationWorkGroup.DisplayName +
                "保存模板普通段子装配体");

            ActivateExpectedAssemblyDocument(
                parentAssemblyPath,
                "插入普通段子装配体前激活总装配体");
            parentAssemblyDocument.EditAssembly();
            var insertedGenerationAssemblyComponent =
                parentAssemblyDocument.AddComponent5(
                    generationAssemblyContext.AssemblyPath,
                    0,
                    string.Empty,
                    false,
                    string.Empty,
                    0d,
                    0d,
                    0d);
            if (insertedGenerationAssemblyComponent == null)
            {
                throw new InvalidOperationException(
                    "普通段子装配体插入总装配体失败。");
            }

            generationAssemblyContext.ParentAssemblyComponent =
                insertedGenerationAssemblyComponent;
            SetIdentityTransform(insertedGenerationAssemblyComponent);
            parentAssemblyModel.ClearSelection2(true);
            if (!insertedGenerationAssemblyComponent.Select(false))
            {
                throw new InvalidOperationException(
                    "无法选中刚插入的普通段子装配体进行固定。");
            }

            parentAssemblyDocument.FixComponent();
            parentAssemblyModel.ClearSelection2(true);
            RebuildGeneratedModel(parentAssemblyModel, "插入铜排子装配体");
            var insertedAssemblyPath =
                insertedGenerationAssemblyComponent.GetPathName();
            if (string.IsNullOrWhiteSpace(insertedAssemblyPath) ||
                !string.Equals(
                    Path.GetFullPath(insertedAssemblyPath),
                    Path.GetFullPath(generationAssemblyContext.AssemblyPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "总装配体中新增子装配体路径异常：" +
                    insertedAssemblyPath);
            }

            SaveAssemblyWithReferences(
                parentAssemblyModel,
                generationWorkGroup.DisplayName +
                "保存总装配体中的普通段子装配体");
            CloseGeneratedAssemblyWindow(generationAssemblyContext);
            ActivateExpectedAssemblyDocument(
                parentAssemblyPath,
                "关闭普通段子装配体后恢复总装配体");
            GenerationLogger.Info(
                "普通段子装配体已按原点插入总装：{0}",
                generationAssemblyContext.AssemblyPath);
            return insertedGenerationAssemblyComponent;
        }

        private bool TryRollbackTemplateGenerationAssembly(
            ModelDoc2 parentAssemblyModel,
            AssemblyDoc parentAssemblyDocument,
            string parentAssemblyPath,
            string outputDirectoryPath,
            AlgorithmBusbarGenerationWorkGroup generationWorkGroup,
            TemplateGenerationAssemblyContext generationAssemblyContext,
            out string rollbackFailureMessage)
        {
            rollbackFailureMessage = null;
            var targetAssemblyPath =
                generationAssemblyContext?.AssemblyPath ??
                Path.Combine(
                    outputDirectoryPath,
                    generationWorkGroup.TargetAssemblyBaseName + ".SLDASM");
            var ownedOutputPaths =
                (generationAssemblyContext?.OwnedOutputPaths ??
                    new List<string>())
                .Where(ownedOutputPath =>
                    !string.IsNullOrWhiteSpace(ownedOutputPath))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            try
            {
                var insertedGenerationAssemblyComponent =
                    generationAssemblyContext?.ParentAssemblyComponent;
                if (insertedGenerationAssemblyComponent != null)
                {
                    ActivateExpectedAssemblyDocument(
                        parentAssemblyPath,
                        "回滚前激活总装配体");
                    parentAssemblyDocument.EditAssembly();
                    parentAssemblyModel.ClearSelection2(true);
                    if (!insertedGenerationAssemblyComponent.Select(false) ||
                        !parentAssemblyDocument.DeleteSelections(
                            (int)swDeleteSelectionOptions_e
                                .swDelete_Children))
                    {
                        rollbackFailureMessage =
                            "无法从总装配体删除本次插入的普通段子装配体。";
                        return false;
                    }

                    parentAssemblyModel.ClearSelection2(true);
                    parentAssemblyModel.EditRebuild3();
                    if (FindTopLevelComponentByPath(
                            parentAssemblyDocument,
                            targetAssemblyPath) != null)
                    {
                        rollbackFailureMessage =
                            "删除后仍能在总装配体顶层找到失败的普通段子装配体。";
                        return false;
                    }

                    SaveAssemblyWithReferences(
                        parentAssemblyModel,
                        "回滚失败的模板普通段子装配体");
                }

                foreach (var ownedOutputPath in ownedOutputPaths
                    .OrderBy(ownedOutputPath => string.Equals(
                        ownedOutputPath,
                        Path.GetFullPath(targetAssemblyPath),
                        StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1))
                {
                    CloseDocumentByPath(ownedOutputPath);
                }

                foreach (var generatedOutputPath in ownedOutputPaths)
                {
                    if (File.Exists(generatedOutputPath))
                    {
                        File.Delete(generatedOutputPath);
                    }
                }

                ActivateExpectedAssemblyDocument(
                    parentAssemblyPath,
                    "回滚后恢复总装配体");
                parentAssemblyDocument.EditAssembly();
                GenerationLogger.Warn(
                    "普通段子装配体生成失败，已清理本组子装配体及铜排文件：{0}",
                    targetAssemblyPath);
                return true;
            }
            catch (Exception rollbackException)
            {
                rollbackFailureMessage = rollbackException.Message;
                GenerationLogger.Error(
                    rollbackException,
                    "回滚失败的模板普通段子装配体时发生异常：{0}",
                    targetAssemblyPath);
                return false;
            }
            finally
            {
                try
                {
                    parentAssemblyModel.ClearSelection2(true);
                }
                catch
                {
                }
            }
        }

        private void CloseGeneratedAssemblyWindow(
            TemplateGenerationAssemblyContext generationAssemblyContext)
        {
            if (generationAssemblyContext == null)
            {
                return;
            }

            CloseDocumentWindow(
                generationAssemblyContext.AssemblyPath,
                generationAssemblyContext.AssemblyTitle);
        }

        private void CloseDocumentByPath(string documentPath)
        {
            CloseDocumentWindow(documentPath, null);
        }

        private void CloseDocumentWindow(
            string documentPath,
            string knownDocumentTitle)
        {
            var openDocumentModel =
                _solidWorksApplication.GetOpenDocumentByName(documentPath)
                    as ModelDoc2;
            var documentTitle = knownDocumentTitle;
            if (string.IsNullOrWhiteSpace(documentTitle) &&
                openDocumentModel != null)
            {
                documentTitle = openDocumentModel.GetTitle();
            }

            if (!string.IsNullOrWhiteSpace(documentTitle))
            {
                _solidWorksApplication.CloseDoc(documentTitle);
            }
        }

        private static Component2 FindTopLevelComponentByPath(
            AssemblyDoc parentAssemblyDocument,
            string expectedComponentPath)
        {
            return (parentAssemblyDocument?.GetComponents(true) as object[] ??
                    Array.Empty<object>())
                .OfType<Component2>()
                .FirstOrDefault(parentComponent =>
                    ComponentPathEquals(
                        parentComponent,
                        expectedComponentPath));
        }

        private ModelDoc2 ActivateExpectedAssemblyDocument(
            string expectedAssemblyPath,
            string activationStageDescription)
        {
            var activationErrors = 0;
            var activatedAssemblyModel =
                _solidWorksApplication.ActivateDoc3(
                    expectedAssemblyPath,
                    false,
                    (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                    ref activationErrors) as ModelDoc2;
            var activatedAssemblyPath =
                activatedAssemblyModel?.GetPathName();
            if (activatedAssemblyModel == null ||
                activatedAssemblyModel.GetType() !=
                    (int)swDocumentTypes_e.swDocASSEMBLY ||
                string.IsNullOrWhiteSpace(activatedAssemblyPath) ||
                !string.Equals(
                    Path.GetFullPath(activatedAssemblyPath),
                    Path.GetFullPath(expectedAssemblyPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}失败，错误码：{1}，实际激活文档：{2}。",
                    activationStageDescription,
                    activationErrors,
                    string.IsNullOrWhiteSpace(activatedAssemblyPath)
                        ? "空"
                        : activatedAssemblyPath));
            }

            return activatedAssemblyModel;
        }

        private static void SaveAssemblyWithReferences(
            ModelDoc2 assemblyModel,
            string saveStageDescription)
        {
            var assemblySaveErrors = 0;
            var assemblySaveWarnings = 0;
            if (assemblyModel == null) throw new InvalidOperationException(saveStageDescription + "文档缺失。");
            var saved = assemblyModel.Save3(
                    (int)(swSaveAsOptions_e.swSaveAsOptions_Silent |
                        swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                    ref assemblySaveErrors,
                    ref assemblySaveWarnings);
            CheckGenerationSave(saved, assemblySaveErrors, assemblySaveWarnings,
                saveStageDescription + "：" + assemblyModel.GetPathName());
        }

        private static bool ComponentPathEquals(
            Component2 component,
            string expectedComponentPath)
        {
            if (component == null ||
                string.IsNullOrWhiteSpace(expectedComponentPath))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(component.GetPathName()),
                    Path.GetFullPath(expectedComponentPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private Component2 CreateAndInsertBusbar(
            ModelDoc2 containingAssemblyModel,
            AssemblyDoc containingAssemblyDocument,
            string containingAssemblyPath,
            string outputDirectoryPath,
            string templateDirectory,
            AlgorithmBusbarGenerationItem generationItem,
            DrawingNumberMetadataSnapshot drawingNumberMetadata,
            ICollection<string> ownedOutputPaths)
        {
            if (generationItem.SourceGenerationItem != null)
            {
                return InsertSourceBusbarInstance(
                    containingAssemblyModel,
                    containingAssemblyDocument,
                    containingAssemblyPath,
                    outputDirectoryPath,
                    generationItem);
            }

            if (generationItem.VerticalBendSetting != null)
            {
                return CreateAndInsertVerticalBendBusbar(
                    containingAssemblyModel,
                    outputDirectoryPath,
                    templateDirectory,
                    generationItem,
                    drawingNumberMetadata,
                    ownedOutputPaths);
            }

            var bendSetting = generationItem.BendSetting;
            bendSetting.GetPointGather();
            if (bendSetting.PointGather == null || bendSetting.PointGather.Count < 2)
            {
                throw new InvalidOperationException(
                    "Bend 铜排路径至少需要两个有效点。");
            }

            if (bendSetting.WidthVector == null ||
                bendSetting.WidthVector.Norm <= DirectionTolerance)
            {
                throw new InvalidOperationException("Bend 铜排排宽方向无效。");
            }

            var sourceTemplatePath = Path.Combine(
                templateDirectory,
                generationItem.TemplateName + ".SLDPRT");
            var generatedPartPath = Path.Combine(
                outputDirectoryPath,
                generationItem.TargetPartBaseName + ".SLDPRT");
            if (File.Exists(generatedPartPath))
            {
                throw new IOException(
                    "目标铜排零件已存在：" + generatedPartPath);
            }

            ModelDoc2 generatedPartModel = null;
            Component2 insertedComponent = null;
            try
            {
                File.Copy(sourceTemplatePath, generatedPartPath, false);
                ownedOutputPaths?.Add(generatedPartPath);

                var openErrors = 0;
                var openWarnings = 0;
                generatedPartModel = _solidWorksApplication.OpenDoc6(
                    generatedPartPath,
                    (int)swDocumentTypes_e.swDocPART,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    string.Empty,
                    ref openErrors,
                    ref openWarnings) as ModelDoc2;
                if (generatedPartModel == null)
                {
                    throw new InvalidOperationException(
                        "无法打开复制后的铜排模板，错误码：" +
                        openErrors + "。");
                }

                ApplyBendParameters(
                    generatedPartModel,
                    bendSetting,
                    string.Equals(
                        generationItem.TemplateName,
                        StraightMilledBusbarTemplateName,
                        StringComparison.OrdinalIgnoreCase),
                    generationItem.TargetPartBaseName,
                    drawingNumberMetadata);

                ActivateExpectedAssemblyDocument(
                    containingAssemblyPath,
                    "插入铜排零件前激活普通段子装配体");
                insertedComponent = containingAssemblyDocument.AddComponent5(
                    generatedPartPath,
                    0,
                    string.Empty,
                    false,
                    string.Empty,
                    0d,
                    0d,
                    0d);
                if (insertedComponent == null)
                {
                    throw new InvalidOperationException(
                        "铜排零件插入当前装配体失败。");
                }

                SetBendTransform(insertedComponent, bendSetting);
                containingAssemblyModel.ClearSelection2(true);
                if (!insertedComponent.Select(false))
                {
                    throw new InvalidOperationException(
                        "无法选中刚插入的铜排零件进行固定。");
                }

                containingAssemblyDocument.FixComponent();

                // 两种铣排模板都保留既有形状草图。直排仅驱动尺寸；桥式
                // 按模板中从原点开始的六点链移动其余五点。普通 Bend 才
                // 使用原有的删除线段并重建路径方式。
                if (string.Equals(
                        generationItem.TemplateName,
                        BridgeMilledBusbarTemplateName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    MoveBridgeMilledSketchPoints(
                        containingAssemblyModel,
                        containingAssemblyDocument,
                        insertedComponent,
                        bendSetting);
                }
                else if (!string.Equals(
                             generationItem.TemplateName,
                             StraightMilledBusbarTemplateName,
                             StringComparison.OrdinalIgnoreCase))
                {
                    RewriteBendSketch(
                        containingAssemblyModel,
                        containingAssemblyDocument,
                        insertedComponent,
                        bendSetting);
                }

                containingAssemblyDocument.EditAssembly();
                var insertedPartModel =
                    insertedComponent.GetModelDoc2() as ModelDoc2 ??
                    generatedPartModel;
                RebuildGeneratedModel(insertedPartModel, "铜排草图驱动后");
                RebuildGeneratedModel(containingAssemblyModel, "铜排装配后");
                RequireCopperComponent(insertedComponent, "铜排保存前");
                var partSaveErrors = 0;
                var partSaveWarnings = 0;
                var saved = insertedPartModel.Save3(
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                        ref partSaveErrors,
                        ref partSaveWarnings);
                CheckGenerationSave(saved, partSaveErrors, partSaveWarnings, "铜排零件：" + generatedPartPath);
                RequireCopperComponent(insertedComponent, "铜排保存后");

                return insertedComponent;
            }
            catch (Exception partCreationException)
            {
                var ownsGeneratedPartPath =
                    ownedOutputPaths?.Contains(
                        generatedPartPath,
                        StringComparer.OrdinalIgnoreCase) == true;
                if (ownsGeneratedPartPath &&
                    !TryRollbackFailedBusbarPart(
                        containingAssemblyModel,
                        containingAssemblyDocument,
                        containingAssemblyPath,
                        insertedComponent,
                        generatedPartPath,
                        ownedOutputPaths,
                        out var rollbackFailureMessage))
                {
                    throw new AlgorithmBusbarPartRollbackException(
                        partCreationException.Message +
                        "；失败铜排零件回滚失败：" +
                        rollbackFailureMessage,
                        partCreationException);
                }

                throw;
            }
        }

        private Component2 CreateAndInsertVerticalBendBusbar(
            ModelDoc2 containingAssemblyModel,
            string outputDirectoryPath,
            string templateDirectory,
            AlgorithmBusbarGenerationItem generationItem,
            DrawingNumberMetadataSnapshot drawingNumberMetadata,
            ICollection<string> ownedOutputPaths)
        {
            var sourceTemplatePath = Path.Combine(
                templateDirectory,
                VerticalBendTemplateName + ".SLDPRT");
            var generatedPartPath = Path.Combine(
                outputDirectoryPath,
                generationItem.TargetPartBaseName + ".SLDPRT");
            if (File.Exists(generatedPartPath))
            {
                throw new IOException(
                    "目标立弯铜排零件已存在：" + generatedPartPath);
            }

            ownedOutputPaths?.Add(generatedPartPath);
            var verticalBendCreator = new BusbarVerticalBendPreview(
                _solidWorksApplication);
            var insertedVerticalBendComponent = verticalBendCreator.BendCreator(
                sourceTemplatePath,
                generatedPartPath,
                generationItem.VerticalBendSetting);
            if (insertedVerticalBendComponent == null)
            {
                throw new InvalidOperationException(
                    "铜排大师立弯生成方法没有返回有效组件。");
            }

            var generatedVerticalBendModel =
                insertedVerticalBendComponent.IGetModelDoc();
            if (generatedVerticalBendModel == null)
                throw new InvalidOperationException("立弯生成后零件文档未解析：" + generatedPartPath);
            {
                ApplyVerticalBendProperties(
                    generatedVerticalBendModel,
                    generationItem,
                    drawingNumberMetadata);
                RebuildGeneratedModel(generatedVerticalBendModel, "立弯铜排驱动后");
                RequireCopperComponent(insertedVerticalBendComponent, "立弯铜排保存前");
                var saveErrors = 0;
                var saveWarnings = 0;
                var saved = generatedVerticalBendModel.Save3(
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                        ref saveErrors,
                        ref saveWarnings);
                CheckGenerationSave(saved, saveErrors, saveWarnings, "立弯铜排：" + generatedPartPath);
                RequireCopperComponent(insertedVerticalBendComponent, "立弯铜排保存后");
            }

            containingAssemblyModel.ClearSelection2(true);
            return insertedVerticalBendComponent;
        }

        private Component2 InsertSourceBusbarInstance(
            ModelDoc2 containingAssemblyModel,
            AssemblyDoc containingAssemblyDocument,
            string containingAssemblyPath,
            string outputDirectoryPath,
            AlgorithmBusbarGenerationItem generationItem)
        {
            var sourceGenerationItem = generationItem.SourceGenerationItem;
            if (sourceGenerationItem == null ||
                string.IsNullOrWhiteSpace(
                    sourceGenerationItem.TargetPartBaseName))
            {
                throw new InvalidOperationException(
                    "同源 Bend 铜排没有可复用的源零件。");
            }

            var sourcePartPath = Path.Combine(
                outputDirectoryPath,
                sourceGenerationItem.TargetPartBaseName + ".SLDPRT");
            if (!File.Exists(sourcePartPath))
            {
                throw new FileNotFoundException(
                    "同源 Bend 铜排的源零件不存在。",
                    sourcePartPath);
            }

            ActivateExpectedAssemblyDocument(
                containingAssemblyPath,
                "插入同源 Bend 铜排前激活铜排子装配体");
            var targetPointGather = generationItem.ShapePointGather;
            var isVerticalBendSourceReuse =
                generationItem.VerticalBendSetting != null;
            var targetWidthVector = generationItem.BendSetting?.WidthVector;
            var targetThicknessVector =
                generationItem.BendSetting?.StartBusbarVector;
            if (isVerticalBendSourceReuse)
            {
                // 立弯源零件的 y 轴方向（首段 -WidthVector）在首次生成时已
                // 固定（BusbarVerticalBendPreview.SetComponentTransform 1093 行
                // 直接取反），与正反序无关。同源复用时必须让组件 y 轴保持源
                // 零件的 y 轴，因此 widthVector/thicknessVector 始终用首段矢
                // 量（WidthVector / StartBusbarVector），不能像平弯那样切到
                // 末段 LastBarVector/LastWidthVector——否则组件会绕轴旋转 90°，
                // 出现"立弯复制后方向镜像"的错误。
                var targetVerticalOrientationSetting =
                    generationItem.VerticalBendSetting.Copy();
                targetVerticalOrientationSetting.GetWidthVector();
                targetVerticalOrientationSetting.SetSegmentPoints(false);
                targetWidthVector = targetVerticalOrientationSetting.WidthVector;
                targetThicknessVector = targetVerticalOrientationSetting.StartBusbarVector;
            }
            var targetBusbarWidth = generationItem.VerticalBendSetting != null
                ? generationItem.VerticalBendSetting.BusBarWidth
                : generationItem.BendSetting?.BusBarWidth ?? 0d;
            if (targetPointGather == null || targetPointGather.Count < 2)
            {
                throw new InvalidOperationException(
                    "同源 Bend 铜排缺少目标定位路径。");
            }
            IReadOnlyList<Point3d> targetPlacementPointGather =
                targetPointGather;
            // 平弯反序复用通过 Reverse 路径点 + 切到末段矢量实现"源零件
            // 180° 旋转对齐到新位置"。立弯源零件的 x/y 轴已在首次生成
            // 固定（x = 首段方向，y = -首段 WidthVector），不能 Reverse
            // 路径点也不能切到末段矢量——否则组件整体旋转 90° 出现镜像。
            // 因此立弯同源（包括反序）时直接使用原序路径点 + 首段矢量。
            var reverseTargetPlacementPointOrder =
                !isVerticalBendSourceReuse &&
                generationItem.SourcePointOrderReversed &&
                (targetPointGather.Count > 2 ||
                 generationItem.BendSetting?.IsMilledBar == true);
            if (reverseTargetPlacementPointOrder)
            {
                targetPlacementPointGather = targetPointGather
                    .Reverse()
                    .ToList()
                    .AsReadOnly();
            }

            var insertedSourceComponent =
                containingAssemblyDocument.AddComponent5(
                    sourcePartPath,
                    0,
                    string.Empty,
                    false,
                    string.Empty,
                    0d,
                    0d,
                    0d);
            if (insertedSourceComponent == null)
            {
                throw new InvalidOperationException(
                    "同源 Bend 铜排源零件再次插入子装配体失败。");
            }

            // 同源只代表零件形状可以复用，不能复用源组件在装配体中的
            // 姿态。按形状匹配的点对应关系以及目标排宽方向重新构造完整
            // 坐标变换，行为与首次生成以及 InsulatingWoodGen 的
            // ComposeTransform 定位方式保持一致。普通两点直排保留目标
            // 路径方向；两点铣排的首末铣削长度可以区分正反，A/C 相
            // 反序同源时必须反转定位点，把复用零件的首末端调换。
            SetBusbarTransform(
                insertedSourceComponent,
                targetPlacementPointGather,
                targetWidthVector,
                targetThicknessVector,
                targetBusbarWidth,
                isVerticalBendSourceReuse);
            containingAssemblyModel.ClearSelection2(true);
            if (!insertedSourceComponent.Select(false))
            {
                throw new InvalidOperationException(
                    "无法选中同源 Bend 铜排组件进行固定。");
            }

            containingAssemblyDocument.FixComponent();
            containingAssemblyDocument.EditAssembly();
            RebuildGeneratedModel(containingAssemblyModel, "同源铜排定位后");
            RequireCopperComponent(insertedSourceComponent, "同源铜排复用后");
            GenerationLogger.Info(
                "同源 Bend 铜排已复用源零件并定位：源={0}，反序定位={1}",
                sourcePartPath,
                reverseTargetPlacementPointOrder);
            return insertedSourceComponent;
        }

        private bool TryRollbackFailedBusbarPart(
            ModelDoc2 containingAssemblyModel,
            AssemblyDoc containingAssemblyDocument,
            string containingAssemblyPath,
            Component2 insertedComponent,
            string generatedPartPath,
            ICollection<string> ownedOutputPaths,
            out string rollbackFailureMessage)
        {
            rollbackFailureMessage = null;
            try
            {
                if (insertedComponent != null)
                {
                    ActivateExpectedAssemblyDocument(
                        containingAssemblyPath,
                        "回滚铜排零件前激活普通段子装配体");
                    containingAssemblyDocument.EditAssembly();
                    containingAssemblyModel.ClearSelection2(true);
                    if (!insertedComponent.Select(false) ||
                        !containingAssemblyDocument.DeleteSelections(
                            (int)swDeleteSelectionOptions_e
                                .swDelete_Children))
                    {
                        rollbackFailureMessage =
                            "无法从当前普通段子装配体删除本次插入的铜排组件。";
                        return false;
                    }

                    containingAssemblyModel.ClearSelection2(true);
                    containingAssemblyModel.EditRebuild3();
                    if (FindTopLevelComponentByPath(
                            containingAssemblyDocument,
                            generatedPartPath) != null)
                    {
                        rollbackFailureMessage =
                            "删除后仍能在当前普通段子装配体顶层找到失败铜排组件。";
                        return false;
                    }
                }

                var openGeneratedPartModel =
                    _solidWorksApplication.GetOpenDocumentByName(
                        generatedPartPath) as ModelDoc2;
                if (openGeneratedPartModel != null)
                {
                    var generatedPartTitle =
                        openGeneratedPartModel.GetTitle();
                    if (!string.IsNullOrWhiteSpace(generatedPartTitle))
                    {
                        _solidWorksApplication.CloseDoc(generatedPartTitle);
                    }
                }

                if (_solidWorksApplication.GetOpenDocumentByName(
                        generatedPartPath) != null)
                {
                    rollbackFailureMessage =
                        "失败铜排零件文档仍处于打开状态。";
                    return false;
                }

                if (File.Exists(generatedPartPath))
                {
                    File.Delete(generatedPartPath);
                }
                ownedOutputPaths?.Remove(generatedPartPath);

                ActivateExpectedAssemblyDocument(
                    containingAssemblyPath,
                    "回滚铜排零件后恢复普通段子装配体");
                containingAssemblyDocument.EditAssembly();
                GenerationLogger.Warn(
                    "铜排零件生成失败，已移除组件并清理本次新建文件：{0}",
                    generatedPartPath);
                return true;
            }
            catch (Exception partRollbackException)
            {
                rollbackFailureMessage = partRollbackException.Message;
                GenerationLogger.Error(
                    partRollbackException,
                    "回滚失败的铜排零件时发生异常：{0}",
                    generatedPartPath);
                return false;
            }
            finally
            {
                try
                {
                    containingAssemblyModel.ClearSelection2(true);
                }
                catch
                {
                }
            }
        }

        private static void ApplyVerticalBendProperties(
            ModelDoc2 generatedVerticalBendModel,
            AlgorithmBusbarGenerationItem generationItem,
            DrawingNumberMetadataSnapshot drawingNumberMetadata)
        {
            var verticalBendSetting = generationItem.VerticalBendSetting;
            if (verticalBendSetting == null)
            {
                throw new InvalidOperationException(
                    "立弯铜排属性写入所需的参数为空。");
            }

            var activeConfigurationName = generatedVerticalBendModel
                    .ConfigurationManager?.ActiveConfiguration?.Name ??
                string.Empty;
            WriteCustomProperty(
                generatedVerticalBendModel,
                activeConfigurationName,
                PartTypePropertyName,
                BusbarPartTypePropertyValue);
            WriteCustomProperty(
                generatedVerticalBendModel,
                activeConfigurationName,
                BusbarShapePropertyName,
                VerticalBendTemplateName);
            WriteCustomProperty(
                generatedVerticalBendModel,
                activeConfigurationName,
                BusbarPhasePropertyName,
                ResolvePhasePropertyValue(verticalBendSetting.Phase));
            WriteCustomProperty(
                generatedVerticalBendModel,
                activeConfigurationName,
                BusbarSpecificationPropertyName,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:0.###}*{1:0.###}",
                    verticalBendSetting.BusBarWidth,
                    verticalBendSetting.BusBarThickness));
            WriteCustomProperty(
                generatedVerticalBendModel,
                activeConfigurationName,
                BusbarBendingRadiusPropertyName,
                "R" + verticalBendSetting.BarBendingRadius.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture));
            WriteDrawingMetadata(
                generatedVerticalBendModel,
                generationItem.TargetPartBaseName,
                drawingNumberMetadata);
        }

        private static void ApplyBendParameters(
            ModelDoc2 generatedPartModel,
            BendPreviewSetting bendSetting,
            bool usesStraightMilledBusbarTemplate,
            string targetPartBaseName,
            DrawingNumberMetadataSnapshot drawingNumberMetadata)
        {
            var generatedBusbarWidth = bendSetting.BusBarWidth;
            if (!SetOptionalDimension(
                    generatedPartModel,
                    generatedBusbarWidth,
                    "W@铜排基体",
                    "D2@铜排基体"))
            {
                throw new InvalidOperationException(
                    "铜排模板中未找到排宽驱动尺寸。");
            }

            if (bendSetting.IsMilledBar)
            {
                var originalMilledBarWidth =
                    bendSetting.MilledBarOriginalWidth;
                if (originalMilledBarWidth <= DirectionTolerance)
                {
                    throw new InvalidOperationException(
                        "铣排缺少有效的排宽W数据。");
                }
                if (!SetOptionalDimension(
                        generatedPartModel,
                        originalMilledBarWidth,
                        "排宽W@草图1"))
                {
                    throw new InvalidOperationException(
                        "铣排模板草图1中未找到“排宽W”尺寸。");
                }
                if (!SetOptionalDimension(
                        generatedPartModel,
                        bendSetting.MilledBarAdditionalWidth,
                        "增加宽@草图1"))
                {
                    throw new InvalidOperationException(
                        "铣排模板草图1中未找到“增加宽”尺寸。");
                }
                var straightMilledBusbarTotalLength =
                    usesStraightMilledBusbarTemplate
                        ? CalculateCenterlineLength(bendSetting.PointGather)
                        : 0d;
                if (usesStraightMilledBusbarTemplate &&
                    (straightMilledBusbarTotalLength <= DirectionTolerance ||
                     !SetOptionalDimension(
                         generatedPartModel,
                         straightMilledBusbarTotalLength,
                         "SL@形状草图")))
                {
                    throw new InvalidOperationException(
                        "铣排模板形状草图中未找到有效的“SL”尺寸。");
                }
                if (bendSetting.MilledBarStartLength <= DirectionTolerance ||
                    !SetOptionalDimension(
                        generatedPartModel,
                        bendSetting.MilledBarStartLength,
                        "SL@草图1"))
                {
                    throw new InvalidOperationException(
                        "铣排模板草图1中未找到有效的“SL”切除位置尺寸。");
                }

                // 当前“增加宽”取两端原始排中心的绝对偏移量；字段独立
                // 保留，后续调整加工规则时不需要再反推几何。
                GenerationLogger.Info(
                    "铣排尺寸：排宽W={0:0.###}mm，增加宽={1:0.###}mm，总排宽={2:0.###}mm，SL总长={3}，SL左端切除长度={4}",
                    originalMilledBarWidth,
                    bendSetting.MilledBarAdditionalWidth,
                    generatedBusbarWidth,
                    usesStraightMilledBusbarTemplate
                        ? straightMilledBusbarTotalLength.ToString(
                            "0.###",
                            CultureInfo.InvariantCulture) + "mm"
                        : "不适用",
                    bendSetting.MilledBarStartLength.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) + "mm");
                ApplyMilledBarFeatureSide(
                    generatedPartModel,
                    bendSetting.MilledBarFeatureSide);
            }

            if (!SetOptionalDimension(
                    generatedPartModel,
                    bendSetting.BusBarThickness,
                    "Thickness@钣金",
                    "厚度@钣金"))
            {
                throw new InvalidOperationException(
                    "铜排模板中未找到排厚驱动尺寸。");
            }

            if (bendSetting.PointGather.Count > 2 &&
                !SetOptionalDimension(
                    generatedPartModel,
                    bendSetting.BarBendingRadius,
                    "R@钣金",
                    "D1@钣金"))
            {
                throw new InvalidOperationException(
                    "铜排模板中未找到折弯半径驱动尺寸。");
            }

            var activeConfigurationName =
                generatedPartModel.ConfigurationManager?.ActiveConfiguration?.Name ??
                string.Empty;
            WriteCustomProperty(
                generatedPartModel,
                activeConfigurationName,
                PartTypePropertyName,
                BusbarPartTypePropertyValue);
            WriteCustomProperty(
                generatedPartModel,
                activeConfigurationName,
                BusbarShapePropertyName,
                bendSetting.IsMilledBar
                    ? "铣排"
                    : bendSetting.Style ?? string.Empty);
            if (bendSetting.IsMilledBar)
            {
                WriteCustomProperty(
                    generatedPartModel,
                    activeConfigurationName,
                    BusbarBendTypePropertyName,
                    "铣排");
            }
            WriteCustomProperty(
                generatedPartModel,
                activeConfigurationName,
                BusbarPhasePropertyName,
                ResolvePhasePropertyValue(bendSetting.Phase));
            WriteCustomProperty(
                generatedPartModel,
                activeConfigurationName,
                BusbarSpecificationPropertyName,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:0.###}*{1:0.###}",
                    generatedBusbarWidth,
                    bendSetting.BusBarThickness));
            WriteCustomProperty(
                generatedPartModel,
                activeConfigurationName,
                BusbarBendingRadiusPropertyName,
                "R" + bendSetting.BarBendingRadius.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture));
            WriteCustomProperty(
                generatedPartModel,
                activeConfigurationName,
                DefaultFixedPropertyName,
                bendSetting.FixedForm.ToString(CultureInfo.InvariantCulture));
            WriteDrawingMetadata(
                generatedPartModel,
                targetPartBaseName,
                drawingNumberMetadata);
            RebuildGeneratedModel(generatedPartModel, "铜排模板尺寸驱动后");
            RequireCopperSolid(generatedPartModel, "铜排模板尺寸驱动后");
        }

        private void SetBendTransform(
            Component2 insertedComponent,
            BendPreviewSetting bendSetting)
        {
            SetBusbarTransform(
                insertedComponent,
                bendSetting.PointGather,
                bendSetting.WidthVector,
                bendSetting.StartBusbarVector,
                bendSetting.BusBarWidth,
                false);
        }

        private void SetBusbarTransform(
            Component2 insertedComponent,
            IReadOnlyList<Point3d> pointGather,
            Vector3d widthVector,
            Vector3d thicknessVector,
            double busbarWidth,
            bool componentZAlignsWithThicknessDirection)
        {
            if (pointGather == null || pointGather.Count < 2)
            {
                throw new InvalidOperationException(
                    "铜排定位所需的路径点不足。");
            }

            if (widthVector == null || widthVector.Norm <= DirectionTolerance)
            {
                throw new InvalidOperationException(
                    "铜排定位所需的排宽方向无效。");
            }

            if (thicknessVector == null ||
                thicknessVector.Norm <= DirectionTolerance)
            {
                throw new InvalidOperationException(
                    "铜排定位所需的排厚方向无效。");
            }

            var pathOriginPoint = pointGather[0];
            var pathXAxisPoint = pointGather[1];
            var pathDirection = new Vector3d(
                pathOriginPoint,
                pathXAxisPoint).Normalized;
            var orientedWidthVector = widthVector.Normalized;
            var calculatedThicknessVector = pathDirection.Cross(
                orientedWidthVector);
            if (calculatedThicknessVector.Norm <= DirectionTolerance)
            {
                throw new InvalidOperationException(
                    "铜排首段方向与排宽方向平行，无法构造定位坐标系。");
            }

            // 排宽方向校正：
            // 1) 普通自由折弯模板的实体沿局部 Z 轴反方向生成，而自由立弯
            //    模板的组件 Z 轴与排厚方向同向。
            // 2) 同源复用时，平弯和立弯路径分开处理：
            //    - 平弯：保持原 thicknessDirectionDot 判定（dot>0 反转）。
            //    - 立弯：立弯同源（包括反序）已在 InsertSourceBusbarInstance
            //      中固定用首段矢量，且不 Reverse 路径点；这里只需把
            //      源零件的 WidthVector 取反得到 y 轴 = -WidthVector，
            //      与首次生成（BusbarVerticalBendPreview.SetComponentTransform
            //      1093 行的 WidthVector.Negate()）保持一致。
            var thicknessDirectionDot = calculatedThicknessVector.Normalized
                .Dot(thicknessVector.Normalized);
            var shouldReverseWidthDirection =
                componentZAlignsWithThicknessDirection
                    ? true
                    : thicknessDirectionDot > 0d;
            if (shouldReverseWidthDirection)
            {
                orientedWidthVector = orientedWidthVector.Negate();
            }

            var pathXYPlanePoint = pathOriginPoint.Move(
                orientedWidthVector,
                busbarWidth / 2d);
            var bendCoordinate = new Coord3d(
                pathOriginPoint,
                pathXAxisPoint,
                pathXYPlanePoint);
            var mathUtility = _solidWorksApplication.IGetMathUtility();
            var originVector = (MathVector)mathUtility.CreateVector(new[]
            {
                pathOriginPoint.X / 1000d,
                pathOriginPoint.Y / 1000d,
                pathOriginPoint.Z / 1000d
            });
            var xAxisVector = (MathVector)mathUtility.CreateVector(new[]
            {
                bendCoordinate.Xaxis.X,
                bendCoordinate.Xaxis.Y,
                bendCoordinate.Xaxis.Z
            });
            var yAxisVector = (MathVector)mathUtility.CreateVector(new[]
            {
                bendCoordinate.Yaxis.X,
                bendCoordinate.Yaxis.Y,
                bendCoordinate.Yaxis.Z
            });
            var zAxisVector = (MathVector)mathUtility.CreateVector(new[]
            {
                bendCoordinate.Zaxis.X,
                bendCoordinate.Zaxis.Y,
                bendCoordinate.Zaxis.Z
            });
            insertedComponent.Transform2 = mathUtility.ComposeTransform(
                xAxisVector,
                yAxisVector,
                zAxisVector,
                originVector,
                1d);
        }

        private void SetIdentityTransform(Component2 targetComponent)
        {
            if (targetComponent == null)
            {
                throw new ArgumentNullException(nameof(targetComponent));
            }

            var identityMathUtility =
                _solidWorksApplication.IGetMathUtility();
            var identityOriginVector =
                (MathVector)identityMathUtility.CreateVector(new[]
                {
                    0d,
                    0d,
                    0d
                });
            var identityXAxisVector =
                (MathVector)identityMathUtility.CreateVector(new[]
                {
                    1d,
                    0d,
                    0d
                });
            var identityYAxisVector =
                (MathVector)identityMathUtility.CreateVector(new[]
                {
                    0d,
                    1d,
                    0d
                });
            var identityZAxisVector =
                (MathVector)identityMathUtility.CreateVector(new[]
                {
                    0d,
                    0d,
                    1d
                });
            var identityTransform = identityMathUtility.ComposeTransform(
                identityXAxisVector,
                identityYAxisVector,
                identityZAxisVector,
                identityOriginVector,
                1d);
            if (identityTransform == null)
            {
                throw new InvalidOperationException(
                    "无法创建普通段子装配体的单位变换矩阵。");
            }

            targetComponent.Transform2 = identityTransform;
        }

        private void MoveBridgeMilledSketchPoints(
            ModelDoc2 containingAssemblyModel,
            AssemblyDoc containingAssemblyDocument,
            Component2 insertedComponent,
            BendPreviewSetting bendSetting)
        {
            if (bendSetting?.PointGather == null ||
                bendSetting.PointGather.Count != 6)
            {
                throw new InvalidOperationException(
                    "桥式铣排正式路径必须包含六个点。");
            }

            containingAssemblyModel.ClearSelection2(true);
            if (!insertedComponent.Select(false))
            {
                throw new InvalidOperationException(
                    "无法选中桥式铣排零件。");
            }

            var editPartError = 0;
            if (containingAssemblyDocument.EditPart2(
                    true,
                    true,
                    ref editPartError) < 0)
            {
                throw new InvalidOperationException(
                    "无法进入桥式铣排模板编辑状态，错误码：" +
                    editPartError + "。");
            }

            try
            {
                containingAssemblyModel.ClearSelection2(true);
                if (!containingAssemblyModel.Extension.SelectByID2(
                        "形状草图@" +
                        insertedComponent.GetSelectByIDString(),
                        "SKETCH",
                        0d,
                        0d,
                        0d,
                        false,
                        0,
                        null,
                        0))
                {
                    throw new InvalidOperationException(
                        "桥式铣排模板中未找到“形状草图”。");
                }

                containingAssemblyModel.SketchManager.InsertSketch(true);
                var activeShapeSketch = containingAssemblyModel
                    .SketchManager.ActiveSketch as Sketch;
                if (activeShapeSketch == null)
                {
                    throw new InvalidOperationException(
                        "无法进入桥式铣排形状草图。");
                }

                var orderedTemplateSketchPoints =
                    GetOrderedBridgeTemplateSketchPoints(activeShapeSketch);
                var targetSketchPoints = bendSetting.PointGather
                    .Select(targetAssemblyPoint => targetAssemblyPoint
                        .ConvertPointInAssemblyToPart1000(
                            _solidWorksApplication,
                            insertedComponent)
                        .UnitX0001Copy()
                        .ConvertModelPointToSketch(
                            _solidWorksApplication,
                            activeShapeSketch))
                    .ToList();
                if (!AreSameSketchPoint(
                        orderedTemplateSketchPoints[0],
                        targetSketchPoints[0]))
                {
                    throw new InvalidOperationException(
                        "桥式铣排形状草图原点与正式路径起点不一致。");
                }

                var originalAddToDatabase = containingAssemblyModel
                    .SketchManager.AddToDB;
                try
                {
                    containingAssemblyModel.SketchManager.AddToDB = true;
                    // 原点受模板固定关系控制，只移动其余五个点。铜排大师
                    // 同样通过 SketchPoint.X/Y 修改既有草图点位置。
                    for (var movablePointIndex = 1;
                        movablePointIndex < orderedTemplateSketchPoints.Count;
                        movablePointIndex++)
                    {
                        orderedTemplateSketchPoints[movablePointIndex].X =
                            targetSketchPoints[movablePointIndex].X;
                        orderedTemplateSketchPoints[movablePointIndex].Y =
                            targetSketchPoints[movablePointIndex].Y;
                    }
                }
                finally
                {
                    containingAssemblyModel.SketchManager.AddToDB =
                        originalAddToDatabase;
                }

                containingAssemblyModel.EditRebuild3();
                containingAssemblyModel.SketchManager.InsertSketch(true);
            }
            finally
            {
                try
                {
                    if (containingAssemblyModel.SketchManager.ActiveSketch !=
                        null)
                    {
                        containingAssemblyModel.SketchManager.InsertSketch(
                            true);
                    }
                }
                finally
                {
                    containingAssemblyDocument.EditAssembly();
                    containingAssemblyModel.ClearSelection2(true);
                }
            }
        }

        private static IReadOnlyList<SketchPoint>
            GetOrderedBridgeTemplateSketchPoints(Sketch activeShapeSketch)
        {
            var templateSketchLines =
                (activeShapeSketch.GetSketchSegments() as object[] ??
                    Array.Empty<object>())
                .OfType<SketchLine>()
                .Where(templateSketchLine =>
                    !((SketchSegment)templateSketchLine)
                        .ConstructionGeometry)
                .ToList();
            if (templateSketchLines.Count != 5)
            {
                throw new InvalidOperationException(
                    "桥式铣排形状草图必须包含五条连续实体线段，当前为" +
                    templateSketchLines.Count + "条。");
            }

            var allLineEndPoints = templateSketchLines
                .SelectMany(templateSketchLine => new[]
                {
                    templateSketchLine.IGetStartPoint2(),
                    templateSketchLine.IGetEndPoint2()
                })
                .ToList();
            var originSketchPoint = allLineEndPoints
                .OrderBy(GetSketchPointDistanceFromOrigin)
                .First();
            if (GetSketchPointDistanceFromOrigin(originSketchPoint) >
                SketchPointMatchToleranceMeters)
            {
                throw new InvalidOperationException(
                    "桥式铣排形状草图的五段线中未找到原点。");
            }

            var orderedSketchPoints = new List<SketchPoint>
            {
                originSketchPoint
            };
            var visitedSketchLineIndexes = new HashSet<int>();
            while (orderedSketchPoints.Count < 6)
            {
                var currentSketchPoint = orderedSketchPoints[
                    orderedSketchPoints.Count - 1];
                var nextSketchLineIndex = -1;
                SketchPoint nextSketchPoint = null;
                for (var templateSketchLineIndex = 0;
                    templateSketchLineIndex < templateSketchLines.Count;
                    templateSketchLineIndex++)
                {
                    if (visitedSketchLineIndexes.Contains(
                            templateSketchLineIndex))
                    {
                        continue;
                    }

                    var templateSketchLine = templateSketchLines[
                        templateSketchLineIndex];
                    var lineStartPoint =
                        templateSketchLine.IGetStartPoint2();
                    var lineEndPoint = templateSketchLine.IGetEndPoint2();
                    if (AreSameSketchPoint(
                            currentSketchPoint,
                            lineStartPoint))
                    {
                        nextSketchLineIndex = templateSketchLineIndex;
                        nextSketchPoint = lineEndPoint;
                        break;
                    }
                    if (AreSameSketchPoint(currentSketchPoint, lineEndPoint))
                    {
                        nextSketchLineIndex = templateSketchLineIndex;
                        nextSketchPoint = lineStartPoint;
                        break;
                    }
                }

                if (nextSketchLineIndex < 0 || nextSketchPoint == null)
                {
                    throw new InvalidOperationException(
                        "桥式铣排形状草图不是从原点开始的连续六点路径。");
                }

                visitedSketchLineIndexes.Add(nextSketchLineIndex);
                orderedSketchPoints.Add(nextSketchPoint);
            }

            if (visitedSketchLineIndexes.Count != templateSketchLines.Count)
            {
                throw new InvalidOperationException(
                    "桥式铣排形状草图包含分支或未连接线段。");
            }

            return orderedSketchPoints.AsReadOnly();
        }

        private static double GetSketchPointDistanceFromOrigin(
            SketchPoint sketchPoint)
        {
            return Math.Sqrt(
                sketchPoint.X * sketchPoint.X +
                sketchPoint.Y * sketchPoint.Y +
                sketchPoint.Z * sketchPoint.Z);
        }

        private static bool AreSameSketchPoint(
            SketchPoint firstSketchPoint,
            SketchPoint secondSketchPoint)
        {
            if (firstSketchPoint == null || secondSketchPoint == null)
            {
                return false;
            }

            var xDifference = firstSketchPoint.X - secondSketchPoint.X;
            var yDifference = firstSketchPoint.Y - secondSketchPoint.Y;
            var zDifference = firstSketchPoint.Z - secondSketchPoint.Z;
            return Math.Sqrt(
                       xDifference * xDifference +
                       yDifference * yDifference +
                       zDifference * zDifference) <=
                   SketchPointMatchToleranceMeters;
        }

        private static bool AreSameSketchPoint(
            SketchPoint firstSketchPoint,
            Point3d secondSketchPoint)
        {
            if (firstSketchPoint == null || secondSketchPoint == null)
            {
                return false;
            }

            var xDifference = firstSketchPoint.X - secondSketchPoint.X;
            var yDifference = firstSketchPoint.Y - secondSketchPoint.Y;
            var zDifference = firstSketchPoint.Z - secondSketchPoint.Z;
            return Math.Sqrt(
                       xDifference * xDifference +
                       yDifference * yDifference +
                       zDifference * zDifference) <=
                   SketchPointMatchToleranceMeters;
        }

        private void RewriteBendSketch(
            ModelDoc2 containingAssemblyModel,
            AssemblyDoc containingAssemblyDocument,
            Component2 insertedComponent,
            BendPreviewSetting bendSetting)
        {
            containingAssemblyModel.ClearSelection2(true);
            if (!insertedComponent.Select(false))
            {
                throw new InvalidOperationException(
                    "无法选中刚插入的铜排零件。");
            }

            var editPartError = 0;
            if (containingAssemblyDocument.EditPart2(
                    true,
                    true,
                    ref editPartError) < 0)
            {
                throw new InvalidOperationException(
                    "无法进入铜排模板零件编辑状态，错误码：" +
                    editPartError + "。");
            }

            var originalInputDimensionOnCreate =
                _solidWorksApplication.GetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            var originalSketchInference =
                _solidWorksApplication.GetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swSketchInference);
            try
            {
                _solidWorksApplication.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swInputDimValOnCreate,
                    false);
                _solidWorksApplication.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swSketchInference,
                    false);
                containingAssemblyModel.ClearSelection2(true);
                var sketchSelected =
                    containingAssemblyModel.Extension.SelectByID2(
                    "形状草图@" + insertedComponent.GetSelectByIDString(),
                    "SKETCH",
                    0d,
                    0d,
                    0d,
                    false,
                    0,
                    null,
                    0);
                if (!sketchSelected)
                {
                    throw new InvalidOperationException(
                        "铜排模板中未找到“形状草图”。");
                }

                containingAssemblyModel.SketchManager.InsertSketch(true);
                var activeSketch = containingAssemblyModel
                    .SketchManager.ActiveSketch as Sketch;
                if (activeSketch == null)
                {
                    throw new InvalidOperationException(
                        "无法进入铜排模板形状草图。");
                }

                var sketchPoints = bendSetting.PointGather
                    .Select(pathPoint => pathPoint
                        .ConvertPointInAssemblyToPart1000(
                            _solidWorksApplication,
                            insertedComponent)
                        .UnitX0001Copy()
                        .ConvertModelPointToSketch(
                            _solidWorksApplication,
                            activeSketch))
                    .ToList();
                sketchPoints = RemoveDuplicatePoints(sketchPoints);
                if (sketchPoints.Count < 2)
                {
                    throw new InvalidOperationException(
                        "铜排路径点不足，无法重建模板草图。");
                }

                var originalAddToDatabase =
                    containingAssemblyModel.SketchManager.AddToDB;
                try
                {
                    containingAssemblyModel.SketchManager.AddToDB = true;
                    var part = insertedComponent.GetModelDoc2() as ModelDoc2;
                    if (part == null) throw new InvalidOperationException("铜排形状草图所属零件未解析。");
                    RewriteSketchKeepingTemplateReference(containingAssemblyModel, part, activeSketch, sketchPoints);
                }
                finally
                {
                    containingAssemblyModel.SketchManager.AddToDB =
                        originalAddToDatabase;
                }

                containingAssemblyModel.SketchManager.InsertSketch(true);
            }
            finally
            {
                try
                {
                    try
                    {
                        if (containingAssemblyModel
                            .SketchManager.ActiveSketch != null)
                        {
                            containingAssemblyModel.SketchManager.InsertSketch(
                                true);
                        }
                    }
                    catch (Exception exitSketchException)
                    {
                        GenerationLogger.Warn(
                            exitSketchException,
                            "母排生成失败清理时退出形状草图失败。");
                    }

                    containingAssemblyDocument.EditAssembly();
                    containingAssemblyModel.ClearSelection2(true);
                }
                finally
                {
                    _solidWorksApplication.SetUserPreferenceToggle(
                        (int)swUserPreferenceToggle_e.swInputDimValOnCreate,
                        originalInputDimensionOnCreate);
                    _solidWorksApplication.SetUserPreferenceToggle(
                        (int)swUserPreferenceToggle_e.swSketchInference,
                        originalSketchInference);
                }
            }
        }

        private static bool SetOptionalDimension(
            ModelDoc2 generatedPartModel,
            double dimensionValueInMillimeters,
            params string[] dimensionNames)
        {
            foreach (var dimensionName in dimensionNames ?? Array.Empty<string>())
            {
                var targetDimension =
                    generatedPartModel.Parameter(dimensionName) as Dimension;
                if (targetDimension == null)
                {
                    continue;
                }

                // 正式生成的铜排参数以 mm 传入；SolidWorks 长度驱动尺寸
                // 的 SystemValue 使用 m。与 BusbarDriver 的原有尺寸驱动规则保持一致。
                targetDimension.SystemValue =
                    dimensionValueInMillimeters / 1000d;
                return true;
            }

            return false;
        }

        private static double CalculateCenterlineLength(
            IReadOnlyList<Point3d> centerlinePoints)
        {
            if (centerlinePoints == null || centerlinePoints.Count < 2)
            {
                return 0d;
            }

            var accumulatedCenterlineLength = 0d;
            for (var centerlinePointIndex = 1;
                centerlinePointIndex < centerlinePoints.Count;
                centerlinePointIndex++)
            {
                accumulatedCenterlineLength += centerlinePoints[
                    centerlinePointIndex - 1].DistanceTo(
                        centerlinePoints[centerlinePointIndex]);
            }

            return accumulatedCenterlineLength;
        }

        private static void ApplyMilledBarFeatureSide(
            ModelDoc2 generatedPartModel,
            MilledBarFeatureSide_e enabledMilledBarFeatureSide)
        {
            if (enabledMilledBarFeatureSide == MilledBarFeatureSide_e.None)
            {
                throw new InvalidOperationException(
                    "铣排缺少有效的左右特征方向。");
            }

            SetRequiredFeatureSuppression(
                generatedPartModel,
                "左铣排",
                enabledMilledBarFeatureSide != MilledBarFeatureSide_e.Left);
            SetRequiredFeatureSuppression(
                generatedPartModel,
                "右铣排",
                enabledMilledBarFeatureSide != MilledBarFeatureSide_e.Right);
            GenerationLogger.Info(
                "铣排模板特征方向：启用={0}，压缩={1}",
                enabledMilledBarFeatureSide == MilledBarFeatureSide_e.Left
                    ? "左铣排"
                    : "右铣排",
                enabledMilledBarFeatureSide == MilledBarFeatureSide_e.Left
                    ? "右铣排"
                    : "左铣排");
        }

        private static void SetRequiredFeatureSuppression(
            ModelDoc2 generatedPartModel,
            string featureName,
            bool shouldSuppressFeature)
        {
            var generatedPartDocument = generatedPartModel as PartDoc;
            if (generatedPartDocument == null)
            {
                throw new InvalidOperationException(
                    "铣排模板文档不是有效零件。");
            }

            var targetFeature =
                generatedPartDocument.FeatureByName(featureName) as Feature;
            if (targetFeature == null)
            {
                throw new InvalidOperationException(
                    "铣排模板中未找到特征“" + featureName + "”。");
            }

            var suppressionSucceeded = targetFeature.SetSuppression2(
                shouldSuppressFeature
                    ? (int)swFeatureSuppressionAction_e.swSuppressFeature
                    : (int)swFeatureSuppressionAction_e.swUnSuppressFeature,
                (int)swInConfigurationOpts_e.swThisConfiguration,
                null);
            if (!suppressionSucceeded ||
                targetFeature.IsSuppressed() != shouldSuppressFeature)
            {
                throw new InvalidOperationException(
                    "铣排模板特征“" + featureName + "”" +
                    (shouldSuppressFeature ? "压缩" : "解压") +
                    "失败。");
            }
        }

        private static void WriteCustomProperty(
            ModelDoc2 generatedPartModel,
            string configurationName,
            string propertyName,
            string propertyValue)
        {
            var customPropertyManager =
                generatedPartModel.Extension.CustomPropertyManager[
                    configurationName ?? string.Empty];
            customPropertyManager?.Add3(
                propertyName,
                (int)swCustomInfoType_e.swCustomInfoText,
                propertyValue ?? string.Empty,
                (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
        }

        private static void WriteDrawingMetadata(
            ModelDoc2 targetModel,
            string prefixedDrawingNumber,
            DrawingNumberMetadataSnapshot drawingNumberMetadata)
        {
            if (targetModel == null || drawingNumberMetadata == null)
            {
                throw new ArgumentNullException(
                    targetModel == null
                        ? nameof(targetModel)
                        : nameof(drawingNumberMetadata));
            }

            WriteCustomProperty(
                targetModel,
                string.Empty,
                DrawingNumberPropertyName,
                prefixedDrawingNumber);
            WriteCustomProperty(
                targetModel,
                string.Empty,
                DesignerPropertyName,
                drawingNumberMetadata.Designer);
            WriteCustomProperty(
                targetModel,
                string.Empty,
                ProofreaderPropertyName,
                drawingNumberMetadata.Proofreader);
            WriteCustomProperty(
                targetModel,
                string.Empty,
                ReviewerPropertyName,
                drawingNumberMetadata.Reviewer);
            WriteCustomProperty(
                targetModel,
                string.Empty,
                ApproverPropertyName,
                drawingNumberMetadata.Approver);
        }

        private static string ResolveFreeBendTemplateDirectory()
        {
            var checkedTemplateDirectories = new List<string>();
            var currentAssemblyDirectory = BusBridgeDriver.RegDllPath();
            AddTemplateDirectoryCandidate(
                checkedTemplateDirectories,
                currentAssemblyDirectory);

            foreach (var loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string loadedAssemblyName;
                string loadedAssemblyLocation;
                try
                {
                    loadedAssemblyName = loadedAssembly.GetName().Name;
                    loadedAssemblyLocation = loadedAssembly.Location;
                }
                catch
                {
                    continue;
                }

                if (!string.Equals(
                        loadedAssemblyName,
                        "CopperBarLinkage",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(loadedAssemblyLocation))
                    AddTemplateDirectoryCandidate(
                        checkedTemplateDirectories,
                        Path.GetDirectoryName(loadedAssemblyLocation));
            }

            try
            {
                using (var addinRegistrationKey = Registry.ClassesRoot.OpenSubKey(
                    "CLSID\\" + CopperBarLinkageAddinClassId +
                    "\\InprocServer32"))
                {
                    var codeBaseText =
                        addinRegistrationKey?.GetValue("CodeBase") as string;
                    if (!string.IsNullOrWhiteSpace(codeBaseText) &&
                        Uri.TryCreate(
                            codeBaseText,
                            UriKind.Absolute,
                            out var codeBaseUri) &&
                        codeBaseUri.IsFile)
                    {
                        AddTemplateDirectoryCandidate(
                            checkedTemplateDirectories,
                            Path.GetDirectoryName(codeBaseUri.LocalPath));
                    }
                }
            }
            catch (Exception registryException)
            {
                GenerationLogger.Warn(
                    registryException,
                    "从注册表定位铜排联动模板失败。");
            }

            foreach (var checkedTemplateDirectory in checkedTemplateDirectories)
            {
                if (Directory.Exists(checkedTemplateDirectory))
                {
                    return checkedTemplateDirectory;
                }
            }

            throw new DirectoryNotFoundException(
                "未找到 CopperBarLinkage 自由折弯铜排模板目录。已检查：" +
                string.Join("；", checkedTemplateDirectories));
        }

        private static void AddTemplateDirectoryCandidate(
            ICollection<string> targetDirectories,
            string assemblyDirectory)
        {
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                return;
            }

            var templateDirectory = Path.Combine(
                assemblyDirectory,
                BusbarTemplateRootFolderName,
                FreeBendTemplateFolderName);
            if (!targetDirectories.Contains(
                    templateDirectory,
                    StringComparer.OrdinalIgnoreCase))
            {
                targetDirectories.Add(templateDirectory);
            }
        }

        private static List<Point3d> RemoveDuplicatePoints(
            IEnumerable<Point3d> sourcePoints)
        {
            var uniquePoints = new List<Point3d>();
            foreach (var sourcePoint in sourcePoints ?? Enumerable.Empty<Point3d>())
            {
                if (sourcePoint == null ||
                    uniquePoints.Count > 0 &&
                    uniquePoints[uniquePoints.Count - 1].DistanceTo(sourcePoint) < 0.000001d)
                {
                    continue;
                }

                uniquePoints.Add(sourcePoint);
            }

            return uniquePoints;
        }

        private static string ResolvePhaseName(BusbarPhase_e? busbarPhase)
        {
            switch (busbarPhase ?? BusbarPhase_e.Unknow)
            {
                case BusbarPhase_e.A:
                    return "A";
                case BusbarPhase_e.B:
                    return "B";
                case BusbarPhase_e.C:
                    return "C";
                case BusbarPhase_e.N:
                    return "N";
                case BusbarPhase_e.PE:
                    return "PE";
                default:
                    return string.Empty;
            }
        }

        private static string ResolvePhasePropertyValue(
            BusbarPhase_e? busbarPhase)
        {
            var phaseName = ResolvePhaseName(busbarPhase);
            return string.IsNullOrWhiteSpace(phaseName)
                ? "空相序"
                : phaseName + "相";
        }

    }

    internal sealed class AlgorithmBusbarGenerationItem
    {
        public string BridgeKey { get; set; }
        public PlanarBendBusbarSetting BridgeSetting { get; set; }
        public string DrawingNumber { get; set; }
        public int DrawingNumberOffset { get; set; }
        public string TargetPartBaseName { get; set; }
        public string TemplateName { get; set; }
        public BendPreviewSetting BendSetting { get; set; }
        public VerticalBendSetting VerticalBendSetting { get; set; }
        public IReadOnlyList<Point3d> ShapePointGather { get; set; }
        public BusbarGenerationDurationType DurationType { get; set; }
        public int SourceGroupIndex { get; set; }
        public int? RestrictedSourceGroupIndex { get; set; }
        public AlgorithmBusbarGenerationItem SourceGenerationItem { get; set; }
        public bool SourcePointOrderReversed { get; set; }
    }

    internal sealed class LogicalGenerationBusbar
    {
        public PlanarBendBusbarSetting BusbarSetting { get; set; }
        public bool IsBridgeMilledBar { get; set; }
    }

    internal sealed class AlgorithmBusbarGenerationWorkGroup
    {
        public string DisplayName { get; set; }
        public string DrawingNumber { get; set; }
        public int DrawingNumberOffset { get; set; }
        public string TargetAssemblyBaseName { get; set; }
        public List<AlgorithmBusbarGenerationItem> GenerationItems { get; } =
            new List<AlgorithmBusbarGenerationItem>();
    }

    internal sealed class TemplateGenerationAssemblyContext
    {
        public string AssemblyPath { get; set; }
        public string AssemblyTitle { get; set; }
        public ModelDoc2 AssemblyModel { get; set; }
        public AssemblyDoc AssemblyDocument { get; set; }
        public Component2 ParentAssemblyComponent { get; set; }
        public List<string> CreatedPartPaths { get; } = new List<string>();
        public List<string> OwnedOutputPaths { get; } = new List<string>();
    }

    internal sealed class AlgorithmBusbarSubassemblyRollbackException :
        InvalidOperationException
    {
        public AlgorithmBusbarSubassemblyRollbackException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class AlgorithmBusbarPartRollbackException :
        InvalidOperationException
    {
        public AlgorithmBusbarPartRollbackException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class AlgorithmBusbarGenerationResult
    {
        public Dictionary<string, BridgeCopperPartBinding> Parts { get; } = new Dictionary<string, BridgeCopperPartBinding>(StringComparer.Ordinal);
        public int RequestedBusbarCount { get; set; }
        public int RequestedAssemblyCount { get; set; }
        public string OutputDirectoryPath { get; set; }
        public List<string> CreatedPartPaths { get; } = new List<string>();
        public List<string> CreatedAssemblyPaths { get; } =
            new List<string>();
        public List<string> Diagnostics { get; } = new List<string>();

        public bool IsSuccess =>
            RequestedBusbarCount > 0 &&
            CreatedPartPaths.Count == RequestedBusbarCount &&
            RequestedAssemblyCount > 0 &&
            CreatedAssemblyPaths.Count == RequestedAssemblyCount &&
            Diagnostics.Count == 0;

        public string BuildSummaryMessage()
        {
            if (CreatedPartPaths.Count == 0)
            {
                return Diagnostics.Count == 0
                    ? "未生成任何母排零件。"
                    : "母排生成失败：" + string.Join("；", Diagnostics.Take(3));
            }

            var summaryMessage = string.Format(
                CultureInfo.CurrentCulture,
                "已生成母排零件 {0}/{1} 个、普通段子装配体 {2}/{3} 个，保存目录：{4}。",
                CreatedPartPaths.Count,
                RequestedBusbarCount,
                CreatedAssemblyPaths.Count,
                RequestedAssemblyCount,
                OutputDirectoryPath);
            if (Diagnostics.Count > 0)
            {
                summaryMessage += "已成功生成的零件会保留；部分失败：" +
                    string.Join("；", Diagnostics.Take(3));
            }

            return summaryMessage;
        }
    }
}
